using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Runs the agent once per <c>chat.send</c>, one run per repo at a time, off the hub invocation; stores the user's message (<c>prompt</c>)
/// and the run's events and pushes them (<c>chat.event</c>) to every connection subscribed to the repo (<c>chat.open</c>), plus
/// <c>chat.sessions</c> when a run starts and ends.
/// Every run ends with exactly one stored <c>result</c>: Claude's own, or <c>ok:false</c> for a cancel, the idle timeout, the server stopping,
/// a start failure, an exit without a result line or an unexpected error.
/// Streaming <c>text</c> deltas are pushed with <c>seq</c> 0 and never stored: the following <c>message</c> replaces them, and catching up
/// after a reconnect (<c>chat.history</c>) uses stored events only. Disposing (the host shutting down) stops the runs.
/// </summary>
public sealed class ChatService : IDisposable
{
	/// <summary>Pushes a connection may have pending; beyond that it is dropped (and aborted) so that it reconnects and catches up.</summary>
	public const int MaxPendingPushes = 1_000;

	/// <summary>Room kept in a <c>chat.events</c> page for its own fields and the separators between events.</summary>
	private const int PageReserveBytes = 1_024;

	/// <summary>Largest stored event, so that a page with it alone still fits <see cref="ChatLimits.MaxEventBytes"/>.</summary>
	private const int StoredEventBytes = ChatLimits.MaxEventBytes - PageReserveBytes;

	private const int TitleChars = 60;

	/// <summary>How long a process may take to exit once its output is over.</summary>
	private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

	private readonly IChatStore _store;
	private readonly IMirrorStore _mirror;
	private readonly IProjectStore _projects;
	private readonly IAgentRunner _runner;
	private readonly TimeProvider _time;
	private readonly ILogger<ChatService> _logger;
	private readonly ILogger<StreamJsonParser>? _parserLogger;
	private readonly PermissionBroker _broker;
	private readonly IApprovalEndpoint? _approval;
	private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);

	// No timer inside: nothing to free, and a late Send during shutdown must not hit a disposed source.
	private readonly CancellationTokenSource _stopping = new();

	public ChatService(
		IChatStore store,
		IMirrorStore mirror,
		IProjectStore projects,
		IAgentRunner runner,
		TimeProvider time,
		ILogger<ChatService> logger,
		PermissionBroker? broker = null,
		IApprovalEndpoint? approval = null,
		ILogger<StreamJsonParser>? parserLogger = null)
	{
		_store = store;
		_mirror = mirror;
		_projects = projects;
		_runner = runner;
		_time = time;
		_logger = logger;
		_parserLogger = parserLogger;
		_broker = broker ?? new PermissionBroker(projects, time, NullLogger<PermissionBroker>.Instance);
		_approval = approval;
	}

	/// <summary><c>chat.open</c>: subscribes the connection to the repo's chat (instead of any other repo) and lists its sessions.</summary>
	public ChatSessionsPayload Open(string? repo, EnvelopeContext subscriber)
	{
		var name = RepoOf(repo, MessageTypes.ChatOpen);
		Subscribe(name, subscriber);
		return Sessions(name);
	}

	/// <summary>The connection is gone: it gets no more pushes (its runs go on).</summary>
	public void Unsubscribe(string connectionId) => _subscribers.TryRemove(connectionId, out _);

	/// <summary><c>chat.history</c>: one page of stored events after <paramref name="afterSeq"/>, at most <see cref="ChatLimits.MaxEventBytes"/> serialized.</summary>
	public ChatEventsPayload History(string? sessionId, long afterSeq)
	{
		if (sessionId is null || _store.SessionRepo(sessionId) is null)
		{
			throw new EnvelopeException(ErrorCodes.NotFound, "Unknown chat session.");
		}

		var (events, final) = _store.Read(sessionId, Math.Max(0, afterSeq), StoredEventBytes);
		return new ChatEventsPayload(sessionId, events, final);
	}

	/// <summary>
	/// <c>chat.send</c>: stores and pushes the <c>prompt</c>, starts a run (a new session without <c>sessionId</c>) and returns at once;
	/// the sender is subscribed to the repo. The first events may reach a client before this reply does.
	/// </summary>
	public ChatStartedPayload Send(ChatSendPayload payload, EnvelopeContext sender)
	{
		var repo = RepoOf(payload.Repo, MessageTypes.ChatSend);
		var text = payload.Text;
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "chat.send needs the message in 'text'.");
		}

		if (text.Length > ChatLimits.MaxTextChars)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"A message may have at most {ChatLimits.MaxTextChars} characters.");
		}

		var sessionId = string.IsNullOrEmpty(payload.SessionId) ? null : payload.SessionId;
		if (sessionId is not null && !string.Equals(_store.SessionRepo(sessionId), repo, StringComparison.OrdinalIgnoreCase))
		{
			throw new EnvelopeException(ErrorCodes.NotFound, "Unknown chat session.");
		}

		var folder = _mirror.RepoFolder(repo) ?? throw new EnvelopeException(ErrorCodes.BadRequest, "Open the folder and let it sync first.");
		Subscribe(repo, sender);
		var run = new Run(repo, Guid.NewGuid().ToString("N"), _stopping.Token);
		if (!_runs.TryAdd(repo, run))
		{
			run.Dispose();
			throw new EnvelopeException(ErrorCodes.Busy, "Claude is already working in this folder; wait for it or stop it.");
		}

		try
		{
			// Everything that can fail is read before a new session is created, so that a failure leaves no empty session.
			var settings = _projects.GetSettings(repo);
			run.ClaudeId = sessionId is null ? null : _store.GetClaudeSession(sessionId);
			run.SessionId = sessionId ?? _store.CreateSession(repo, Title(text)).Id;

			var (url, token) = Approval(run, folder, settings.AgentPermissionsOrDefault);
			var agentRun = new AgentRun(
				folder,
				text,
				run.ClaudeId,
				settings.AgentPermissionsOrDefault,
				string.IsNullOrWhiteSpace(settings.AgentModel) ? null : settings.AgentModel,
				settings.AgentAllowedTools,
				url,
				token);

			// Stored before the run starts: its seq precedes the run's events.
			Store(run, [.. Parts(run, new ChatEvent(run.SessionId, run.Id, 0, ChatEventKinds.Prompt, Text: text))]);
			run.Task = Task.Run(() => ExecuteAsync(run, agentRun));
		}
		catch
		{
			_broker.CancelRun(run.Id);
			_runs.TryRemove(new KeyValuePair<string, Run>(repo, run));
			run.Dispose();
			throw;
		}

		return new ChatStartedPayload(run.SessionId, run.Id);
	}

	/// <summary><c>chat.cancel</c>: stops the run (its process tree is killed); unknown or finished runs are ignored.</summary>
	public void Cancel(string? runId)
	{
		foreach (var run in _runs.Values.Where(r => r.Id == runId))
		{
			run.RequestCancel();
		}
	}

	/// <summary><c>chat.approve</c>: answers a pending permission request (<c>not_found</c> when there is none, <c>bad_request</c> for an unknown decision).</summary>
	public void Approve(ChatApprovePayload payload) => _broker.Answer(payload.RunId, payload.RequestId, payload.Decision);

	/// <summary>Stops every run (each ends with a stored <c>result</c>) and waits a few seconds for them.</summary>
	public void Dispose()
	{
		if (_stopping.IsCancellationRequested)
		{
			return;
		}

		_stopping.Cancel();
		var running = _runs.Values.Select(r => r.Task ?? Task.CompletedTask).ToArray();
		if (!Task.WaitAll(running, TimeSpan.FromSeconds(5)))
		{
			_logger.LogWarning("Chat runs did not finish within 5 seconds of shutdown");
		}
	}

	private static string RepoOf(string? repo, string type) =>
		RepoName.Sanitize(repo) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{type} needs the folder name in 'repo'.");

	/// <summary>The first <see cref="TitleChars"/> characters of the message, its lines joined by spaces.</summary>
	private static string Title(string text)
	{
		var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		if (line.Length <= TitleChars)
		{
			return line;
		}

		return line[..(char.IsHighSurrogate(line[TitleChars - 1]) ? TitleChars - 1 : TitleChars)];
	}

	/// <summary>A failed <c>result</c> whose error is capped like a summary.</summary>
	private static ChatEvent Failed(Run run, string error) =>
		new(run.SessionId!, run.Id, 0, ChatEventKinds.Result, Ok: false, Error: ChatEventSplitter.Truncate(error));

	/// <summary>The event with the run's ids, split so that each part still fits a page once stored (the widest seq is assumed).</summary>
	private static IEnumerable<ChatEvent> Parts(Run run, ChatEvent e) =>
		ChatEventSplitter.Split(e with { SessionId = run.SessionId!, RunId = run.Id, Seq = long.MaxValue }, StoredEventBytes).Select(p => p with { Seq = 0 });

	/// <summary>The approval endpoint and the run's token for <c>ask</c> when the Server hosts the endpoint; otherwise none.</summary>
	private (string? Url, string? Token) Approval(Run run, string folder, string permissions)
	{
		if (permissions != "ask")
		{
			return (null, null);
		}

		if (_approval?.Url is not { } url)
		{
			_logger.LogWarning("Chat run {RunId}: no approval endpoint, so Claude may edit files but is denied anything that needs approval", run.Id);
			return (null, null);
		}

		return (url, _broker.StartRun(run.Id, run.Repo, folder, e => Publish(run, e)));
	}

	/// <summary>A broker event: stored and pushed; the idle timer is paused while a request waits for the user.</summary>
	private void Publish(Run run, ChatEvent e)
	{
		run.Waiting(e.Kind == ChatEventKinds.Permission ? 1 : e.Kind == ChatEventKinds.PermissionResolved ? -1 : 0);
		Store(run, [.. Parts(run, e)]);
	}

	private void Subscribe(string repo, EnvelopeContext context)
	{
		var subscriber = new Subscriber(repo, context, _logger);

		// The same repo again keeps the existing queue, so pushes already queued stay in order.
		_subscribers.AddOrUpdate(context.ConnectionId, subscriber, (_, old) => string.Equals(old.Repo, repo, StringComparison.OrdinalIgnoreCase) ? old : subscriber);
	}

	private ChatSessionsPayload Sessions(string repo)
	{
		var current = _runs.TryGetValue(repo, out var run) ? run.SessionId : null;
		return new ChatSessionsPayload(repo, [.. _store.ListSessions(repo).Select(s => s with { Running = s.Id == current })]);
	}

	private void Push(string repo, Envelope envelope)
	{
		foreach (var subscriber in _subscribers.Values.Where(s => string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase)))
		{
			// Too far behind: drop just this subscription and the connection; the client reconnects and catches up with chat.history.
			if (!subscriber.TryEnqueue(envelope) && _subscribers.TryRemove(new KeyValuePair<string, Subscriber>(subscriber.ConnectionId, subscriber)))
			{
				_logger.LogWarning("Chat: connection {ConnectionId} fell {Count} pushes behind; dropping it", subscriber.ConnectionId, MaxPendingPushes);
				subscriber.Abort();
			}
		}
	}

	private void PushEvents(string repo, IEnumerable<ChatEvent> events)
	{
		foreach (var e in events)
		{
			Push(repo, Envelope.Create(MessageTypes.ChatEvent, e));
		}
	}

	private async Task ExecuteAsync(Run run, AgentRun agentRun)
	{
		ChatEvent? result = null;
		IAgentProcess? process = null;
		CancellationTokenSource? idle = null;
		try
		{
			idle = run.StartIdle(_runner.IdleTimeout, _time);
			Push(run.Repo, Envelope.Create(MessageTypes.ChatSessions, Sessions(run.Repo)));
			try
			{
				process = await _runner.StartAsync(agentRun, run.Token);
			}
			catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or Win32Exception)
			{
				// Could not start: the runner's message says what to do (e.g. set Agent:Command).
				result = Failed(run, ex.Message);
			}

			if (process is not null)
			{
				result = await ReadAsync(run, process, idle);
			}
		}
		catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
		{
			result = Failed(run, run.CancelRequested ? "Cancelled." : "The server stopped.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Chat run {RunId} in {Repo} failed", run.Id, run.Repo);
			result = Failed(run, "The run failed; see the server log.");
		}
		finally
		{
			// Pending approvals are denied (and stored) before the result.
			_broker.CancelRun(run.Id);

			// The process never outlives its run; killing also closes the run's job, which ends children it left in the background.
			process?.Kill();
			process?.Dispose();
			idle?.Dispose();
			Finish(run, result);
		}
	}

	/// <summary>Reads the process's output until its <c>result</c> line, its end or a kill (cancel, idle timeout, shutdown); returns the run's <c>result</c>.</summary>
	private async Task<ChatEvent> ReadAsync(Run run, IAgentProcess process, CancellationTokenSource idle)
	{
		var parser = new StreamJsonParser(_parserLogger);
		ChatEvent? result = null;
		using (var stop = CancellationTokenSource.CreateLinkedTokenSource(run.Token, idle.Token))
		{
			using (stop.Token.Register(process.Kill))
			{
				try
				{
					await foreach (var line in process.Lines.WithCancellation(stop.Token))
					{
						run.Touch();
						result = Handle(run, parser.Feed(line));
						if (parser.ClaudeSessionId is { } claudeId && claudeId != run.ClaudeId)
						{
							_store.SetClaudeSession(run.SessionId!, claudeId);
							run.ClaudeId = claudeId;
						}

						if (result is not null)
						{
							// The answer is complete; anything Claude prints after its result line is ignored.
							break;
						}
					}
				}
				catch (OperationCanceledException) when (stop.IsCancellationRequested)
				{
					// Killed: the reason is decided below.
				}
			}
		}

		if (result is not null)
		{
			await ExitedAsync(process);
			return result;
		}

		if (run.Token.IsCancellationRequested)
		{
			return Failed(run, run.CancelRequested ? "Cancelled." : "The server stopped.");
		}

		if (idle.IsCancellationRequested)
		{
			return Failed(run, string.Create(CultureInfo.InvariantCulture, $"Claude sent no output for {_runner.IdleTimeout.TotalMinutes:0.##} minutes, so the run was stopped (Agent:IdleTimeout)."));
		}

		var code = await ExitedAsync(process);
		return Failed(run, process.Stderr is { Length: > 0 } stderr ? stderr : code is null ? "Claude's output ended but it did not exit." : $"Claude exited with code {code} without a result.");
	}

	/// <summary>The exit code; null when the process did not exit within <see cref="ExitGrace"/> (it is killed then; e.g. a child still holds a pipe).</summary>
	private async Task<int?> ExitedAsync(IAgentProcess process)
	{
		try
		{
			return await process.Exited.WaitAsync(ExitGrace, _time);
		}
		catch (TimeoutException)
		{
			process.Kill();
			return null;
		}
	}

	/// <summary>Stores and pushes one line's events in order (deltas pushed only); returns its <c>result</c>, which <see cref="Finish"/> stores.</summary>
	private ChatEvent? Handle(Run run, IReadOnlyList<ChatEvent> parsed)
	{
		ChatEvent? result = null;
		var batch = new List<ChatEvent>();
		foreach (var e in parsed.SelectMany(raw => Parts(run, raw)))
		{
			switch (e.Kind)
			{
				case ChatEventKinds.Result:
					result = e;
					break;
				case ChatEventKinds.Text:
					Store(run, batch);
					Push(run.Repo, Envelope.Create(MessageTypes.ChatEvent, e));
					break;
				default:
					batch.Add(e);
					break;
			}
		}

		Store(run, batch);
		return result;
	}

	private void Store(Run run, List<ChatEvent> batch)
	{
		if (batch.Count > 0)
		{
			// The broker stores from the approval request's thread: one writer at a time keeps the pushes in seq order.
			lock (run.StoreLock)
			{
				PushEvents(run.Repo, _store.Append(run.SessionId!, batch));
			}

			batch.Clear();
		}
	}

	/// <summary>Stores the result, frees the repo for the next run, then tells the subscribers (so a send right after the result is not busy). Never throws.</summary>
	private void Finish(Run run, ChatEvent? result)
	{
		var final = (result ?? Failed(run, "The run failed; see the server log.")) with { SessionId = run.SessionId!, RunId = run.Id, Seq = 0 };
		try
		{
			final = _store.Append(run.SessionId!, [final])[0];
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Chat run {RunId}: storing the result failed", run.Id);
		}

		_runs.TryRemove(new KeyValuePair<string, Run>(run.Repo, run));
		run.Dispose();
		try
		{
			PushEvents(run.Repo, [final]);
			Push(run.Repo, Envelope.Create(MessageTypes.ChatSessions, Sessions(run.Repo)));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Chat run {RunId}: pushing the end of the run failed", run.Id);
		}
	}

	/// <summary>One run: its repo slot, ids and cancellation (linked to the service's shutdown).</summary>
	private sealed class Run(string repo, string id, CancellationToken stopping) : IDisposable
	{
		private readonly CancellationTokenSource _cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
		private readonly Lock _idleLock = new();
		private CancellationTokenSource? _idle;
		private TimeSpan _idleTimeout;
		private int _waiting;

		public string Repo => repo;

		public string Id => id;

		public string? SessionId { get; set; }

		public string? ClaudeId { get; set; }

		public Task? Task { get; set; }

		public CancellationToken Token => _cancel.Token;

		public bool CancelRequested { get; private set; }

		public Lock StoreLock { get; } = new();

		/// <summary>Creates the idle timer (the caller disposes it); output (<see cref="Touch"/>) restarts it.</summary>
		public CancellationTokenSource StartIdle(TimeSpan timeout, TimeProvider time)
		{
			lock (_idleLock)
			{
				_idleTimeout = timeout;
				_idle = new CancellationTokenSource(timeout, time);
				return _idle;
			}
		}

		/// <summary>Output arrived: the idle timer restarts unless a permission request is waiting for the user.</summary>
		public void Touch() => Waiting(0);

		/// <summary>A permission request started (1) or ended (-1): the idle timer is paused while any waits and restarts after the last.</summary>
		public void Waiting(int change)
		{
			lock (_idleLock)
			{
				_waiting += change;
				try
				{
					_idle?.CancelAfter(_waiting > 0 ? Timeout.InfiniteTimeSpan : _idleTimeout);
				}
				catch (ObjectDisposedException)
				{
					// The run has just finished.
				}
			}
		}

		public void RequestCancel()
		{
			CancelRequested = true;
			try
			{
				_cancel.Cancel();
			}
			catch (ObjectDisposedException)
			{
				// The run has just finished.
			}
		}

		public void Dispose() => _cancel.Dispose();
	}

	/// <summary>
	/// A subscribed connection. Its pushes are chained, so they arrive in order while the run never waits for a slow connection;
	/// at most <see cref="MaxPendingPushes"/> wait. A failed send is logged and skipped (a gone connection is harmless).
	/// </summary>
	private sealed class Subscriber(string repo, EnvelopeContext context, ILogger logger)
	{
		private readonly Lock _lock = new();
		private Task _tail = Task.CompletedTask;
		private int _pending;

		public string Repo => repo;

		public string ConnectionId => context.ConnectionId;

		/// <returns>False when <see cref="MaxPendingPushes"/> are already waiting (nothing is queued).</returns>
		public bool TryEnqueue(Envelope envelope)
		{
			lock (_lock)
			{
				if (_pending >= MaxPendingPushes)
				{
					return false;
				}

				_pending++;
				_tail = SendAfterAsync(_tail, envelope);
				return true;
			}
		}

		public void Abort() => context.Abort();

		private async Task SendAfterAsync(Task previous, Envelope envelope)
		{
			await previous;
			try
			{
				await context.SendAsync(envelope, CancellationToken.None);
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "Chat push to {ConnectionId} failed", context.ConnectionId);
			}
			finally
			{
				lock (_lock)
				{
					_pending--;
				}
			}
		}
	}
}
