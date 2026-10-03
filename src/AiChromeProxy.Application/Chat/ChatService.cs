using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Runs the agent once per <c>chat.send</c>, one run per repo at a time, off the hub invocation; stores the run's events and pushes them
/// (<c>chat.event</c>) to every connection subscribed to the repo (<c>chat.open</c>), plus <c>chat.sessions</c> when a run starts and ends.
/// Every run ends with exactly one stored <c>result</c>: Claude's own, or <c>ok:false</c> for a cancel, the idle timeout, the server stopping,
/// a start failure or an exit without a result line.
/// Streaming <c>text</c> deltas are pushed with <c>seq</c> 0 and never stored: the following <c>message</c> replaces them, and catching up
/// after a reconnect (<c>chat.history</c>) uses stored events only. Disposing (the host shutting down) stops the runs.
/// </summary>
public sealed class ChatService : IDisposable
{
	/// <summary>Room left in a <c>chat.events</c> page for its own fields and the separators between events.</summary>
	private const int PageReserveBytes = 1024;

	private const int TitleChars = 60;

	private readonly IChatStore _store;
	private readonly IMirrorStore _mirror;
	private readonly IProjectStore _projects;
	private readonly IAgentRunner _runner;
	private readonly TimeProvider _time;
	private readonly ILogger<ChatService> _logger;
	private readonly ILogger<StreamJsonParser>? _parserLogger;
	private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);

	// No timer inside: nothing to free, and a late Send during shutdown must not hit a disposed source.
	private readonly CancellationTokenSource _stopping = new();

	public ChatService(IChatStore store, IMirrorStore mirror, IProjectStore projects, IAgentRunner runner, TimeProvider time, ILogger<ChatService> logger, ILogger<StreamJsonParser>? parserLogger = null)
	{
		_store = store;
		_mirror = mirror;
		_projects = projects;
		_runner = runner;
		_time = time;
		_logger = logger;
		_parserLogger = parserLogger;
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

	/// <summary><c>chat.history</c>: one page of stored events after <paramref name="afterSeq"/>.</summary>
	public ChatEventsPayload History(string? sessionId, long afterSeq)
	{
		if (sessionId is null || _store.SessionRepo(sessionId) is null)
		{
			throw new EnvelopeException(ErrorCodes.NotFound, "Unknown chat session.");
		}

		var (events, final) = _store.Read(sessionId, Math.Max(0, afterSeq), ChatLimits.MaxEventBytes - PageReserveBytes);
		return new ChatEventsPayload(sessionId, events, final);
	}

	/// <summary>
	/// <c>chat.send</c>: starts a run (a new session without <c>sessionId</c>) and returns at once; the sender is subscribed to the repo.
	/// The first events may reach a client before this reply does.
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
			run.SessionId = sessionId ?? _store.CreateSession(repo, Title(text)).Id;
			run.ClaudeId = _store.GetClaudeSession(run.SessionId);
			var settings = _projects.GetSettings(repo);

			// Task 7: the approval endpoint (ApprovalUrl, ApprovalToken) goes here; without it `ask` only accepts edits.
			var agentRun = new AgentRun(
				folder,
				text,
				run.ClaudeId,
				settings.AgentPermissionsOrDefault,
				string.IsNullOrWhiteSpace(settings.AgentModel) ? null : settings.AgentModel,
				settings.AgentAllowedTools);
			run.Task = Task.Run(() => ExecuteAsync(run, agentRun));
		}
		catch
		{
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

	/// <summary><c>chat.approve</c>: answers a pending permission request.</summary>
	public void Approve(ChatApprovePayload payload)
	{
		// Task 7: the permission broker answers pending requests; until then there are none.
		throw new EnvelopeException(ErrorCodes.NotFound, "No such permission request.");
	}

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
		new(run.SessionId!, run.Id, 0, ChatEventKinds.Result, Ok: false, Error: ChatEventSplitter.Split(new ChatEvent(string.Empty, string.Empty, 0, ChatEventKinds.Result, Summary: error))[0].Summary);

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
			subscriber.Enqueue(envelope);
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
		using (var idle = new CancellationTokenSource(_runner.IdleTimeout, _time))
		{
			try
			{
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
				process?.Dispose();
			}
		}

		Finish(run, result);
	}

	/// <summary>Reads the process's output until it ends or is killed (cancel, idle timeout, shutdown); returns the run's <c>result</c>.</summary>
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
						idle.CancelAfter(_runner.IdleTimeout);
						result = Handle(run, parser.Feed(line)) ?? result;
						if (parser.ClaudeSessionId is { } claudeId && claudeId != run.ClaudeId)
						{
							_store.SetClaudeSession(run.SessionId!, claudeId);
							run.ClaudeId = claudeId;
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

		var code = await process.Exited;
		return Failed(run, process.Stderr is { Length: > 0 } stderr ? stderr : $"Claude exited with code {code} without a result.");
	}

	/// <summary>Stores and pushes one line's events in order (deltas pushed only); returns its <c>result</c>, held back until the process has ended.</summary>
	private ChatEvent? Handle(Run run, IReadOnlyList<ChatEvent> parsed)
	{
		ChatEvent? result = null;
		var batch = new List<ChatEvent>();
		foreach (var raw in parsed)
		{
			// Split with the ids filled and the widest seq, so that the stored event still fits the limit.
			foreach (var part in ChatEventSplitter.Split(raw with { SessionId = run.SessionId!, RunId = run.Id, Seq = long.MaxValue }))
			{
				var e = part with { Seq = 0 };
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
		}

		Store(run, batch);
		return result;
	}

	private void Store(Run run, List<ChatEvent> batch)
	{
		if (batch.Count > 0)
		{
			PushEvents(run.Repo, _store.Append(run.SessionId!, batch));
			batch.Clear();
		}
	}

	/// <summary>Stores the result, frees the repo for the next run, then tells the subscribers (so a send right after the result is not busy).</summary>
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
		PushEvents(run.Repo, [final]);
		try
		{
			Push(run.Repo, Envelope.Create(MessageTypes.ChatSessions, Sessions(run.Repo)));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Chat run {RunId}: listing the sessions failed", run.Id);
		}
	}

	/// <summary>One run: its repo slot, ids and cancellation (linked to the service's shutdown).</summary>
	private sealed class Run(string repo, string id, CancellationToken stopping) : IDisposable
	{
		private readonly CancellationTokenSource _cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);

		public string Repo => repo;

		public string Id => id;

		public string? SessionId { get; set; }

		public string? ClaudeId { get; set; }

		public Task? Task { get; set; }

		public CancellationToken Token => _cancel.Token;

		public bool CancelRequested { get; private set; }

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
	/// A subscribed connection. Its pushes are chained, so they arrive in order while the run never waits for a slow connection; a failed
	/// send is logged and skipped (a gone connection is harmless).
	/// ponytail: the chain is unbounded for a live but stalled connection; add a cap (drop the subscriber, it catches up with chat.history) if that shows up.
	/// </summary>
	private sealed class Subscriber(string repo, EnvelopeContext context, ILogger logger)
	{
		private readonly Lock _lock = new();
		private Task _tail = Task.CompletedTask;

		public string Repo => repo;

		public void Enqueue(Envelope envelope)
		{
			lock (_lock)
			{
				_tail = SendAfterAsync(_tail, envelope);
			}
		}

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
		}
	}
}
