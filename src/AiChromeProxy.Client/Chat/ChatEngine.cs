using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Client.Chat;

/// <summary>
/// The browser side of the chat: opens the open folder's chat on the server (<c>chat.open</c>, once the folder is synced and the
/// connection is up, and again after every reconnect), keeps a <see cref="ChatSession"/> per session it has seen and sends, stops and
/// approves. State is keyed on the events' own <c>sessionId</c>/<c>runId</c>: the sender gets the <c>prompt</c> of its run before the
/// <c>chat.started</c> reply, and another tab's runs arrive the same way. After a reconnect the stored events missed are read with
/// <c>chat.history</c> for every session whose history was asked for; replays are dropped by <c>seq</c>.
/// </summary>
public sealed class ChatEngine(ITransport transport, SyncEngine sync, TimeProvider time)
{
	/// <summary>How long a request waits for its reply (a reconnect cancels it sooner).</summary>
	public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	private const string NoFolder = "Open a folder first.";

	private readonly Lock _gate = new();
	private readonly SemaphoreSlim _opening = new(1, 1);
	private readonly Dictionary<string, ChatSession> _sessions = new(StringComparer.Ordinal);
	private IReadOnlyList<ChatSessionInfo> _listed = [];
	private string? _repo;
	private bool _needsOpen = true;

	/// <summary>Bumped whenever the connection is not up: an open that started before it does not count as done.</summary>
	private int _epoch;
	private CancellationTokenSource _epochCts = new();
	private string? _awaitingText;
	private DateTimeOffset? _since;
	private decimal? _liveCost;
	private ChatSession _current = new(null);

	/// <summary>Raised after every visible change; the UI re-renders (it coalesces bursts, e.g. with <c>RenderCoalescer</c>).</summary>
	public event Action? Changed;

	/// <summary>Gets the repo's sessions, newest first, as the server last listed them (<c>running</c> as of then).</summary>
	public IReadOnlyList<ChatSessionInfo> Sessions
	{
		get
		{
			lock (_gate)
			{
				return _listed;
			}
		}
	}

	/// <summary>Gets the session on screen: a known one, or the draft of a new chat (<see cref="ChatSession.IsDraft"/>) that the first message turns into a session.</summary>
	public ChatSession Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	/// <summary>Gets a value indicating whether Claude is working in the repo (one run at a time, whichever session it belongs to).</summary>
	public bool Running
	{
		get
		{
			lock (_gate)
			{
				return IsRunning();
			}
		}
	}

	/// <summary>Gets the time since this page saw the run start, or null while idle.</summary>
	public TimeSpan? Elapsed
	{
		get
		{
			lock (_gate)
			{
				return _since is { } since ? time.GetUtcNow() - since : null;
			}
		}
	}

	/// <summary>Gets the cost of the last run that ended (as this page saw it, else the one of the session on screen).</summary>
	public decimal? LastCost
	{
		get
		{
			lock (_gate)
			{
				return _liveCost ?? _current.LastCost;
			}
		}
	}

	/// <summary>Starts listening to the connection and the folder; opens the chat when both are ready already.</summary>
	public async Task InitializeAsync()
	{
		transport.StateChanged -= OnStateChanged;
		transport.StateChanged += OnStateChanged;
		transport.Received -= OnReceived;
		transport.Received += OnReceived;
		sync.Changed -= OnSyncChanged;
		sync.Changed += OnSyncChanged;
		await EnsureOpenAsync();
	}

	/// <summary><b>New chat</b>: the next message starts a new session.</summary>
	public void NewChat()
	{
		lock (_gate)
		{
			_current = new ChatSession(null);
			_awaitingText = null;
		}

		Raise();
	}

	/// <summary>Shows a session and loads its history (what is missing of it when it was seen before).</summary>
	/// <param name="sessionId">A session of <see cref="Sessions"/>.</param>
	public async Task OpenSessionAsync(string sessionId)
	{
		ChatSession session;
		bool connected;
		CancellationToken token;
		lock (_gate)
		{
			token = _epochCts.Token;
			session = GetOrCreate(sessionId);
			session.Loaded = true;
			_current = session;
			_awaitingText = null;
			connected = _repo is not null && !_needsOpen;
		}

		Raise();
		if (connected)
		{
			await LoadHistoryAsync(session, token);
		}
	}

	/// <summary>Sends a message into the session on screen (a new session when it is a draft). A refusal (<c>busy</c>, too long, ...) becomes an error line of the session.</summary>
	/// <param name="text">The message.</param>
	public async Task SendAsync(string text)
	{
		ChatSession session;
		string? repo;
		CancellationToken token;
		lock (_gate)
		{
			token = _epochCts.Token;
			session = _current;
			repo = _repo ?? sync.Repo;
			_awaitingText = session.IsDraft ? text : null;
		}

		session.ClearErrors();
		if (repo is null)
		{
			session.AddError(NoFolder);
			Raise();
			return;
		}

		try
		{
			var started = Read<ChatStartedPayload>(await RequestAsync(MessageTypes.ChatSend, new ChatSendPayload(repo, session.Id, text), token));
			lock (_gate)
			{
				if (session == _current && session.IsDraft)
				{
					_current = GetOrCreate(started.SessionId);
					_current.Loaded = true;
				}

				_awaitingText = null;
			}
		}
		catch (Exception ex)
		{
			lock (_gate)
			{
				_awaitingText = null;
			}

			session.AddError(Describe(ex));
		}

		Raise();
	}

	/// <summary><b>Stop</b>: cancels the run in progress.</summary>
	public async Task StopAsync()
	{
		string? runId;
		lock (_gate)
		{
			runId = _current.ActiveRunId ?? _sessions.Values.Select(s => s.ActiveRunId).FirstOrDefault(id => id is not null);
		}

		if (runId is not null)
		{
			await TryAsync(MessageTypes.ChatCancel, new ChatCancelPayload(runId));
		}
	}

	/// <summary>Answers a permission request of the session on screen (<see cref="ChatDecisions"/>); the card closes when the server's <c>permissionResolved</c> arrives.</summary>
	/// <param name="requestId">The card's <see cref="ChatApproval.RequestId"/>.</param>
	/// <param name="decision">One of <see cref="ChatDecisions"/>.</param>
	/// <returns>Whether the answer was sent (false: the card is gone, or the request failed, which shows as an error line).</returns>
	public async Task<bool> ApproveAsync(string requestId, string decision)
	{
		ChatApproval? card;
		lock (_gate)
		{
			card = _sessions.Values.SelectMany(s => s.Approvals).FirstOrDefault(a => a.RequestId == requestId);
		}

		return card is not null && await TryAsync(MessageTypes.ChatApprove, new ChatApprovePayload(card.RunId, requestId, decision));
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private string Describe(Exception ex) => ex switch
	{
		RequestFailedException { Code: ErrorCodes.Busy } => "Claude is already working in this folder; wait for it or stop it.",
		RequestFailedException { Code: ErrorCodes.TooLarge } => $"The message is too long (at most {ChatLimits.MaxTextChars} characters).",
		RequestFailedException { Code: ErrorCodes.NotFound } => "This chat no longer exists on the server.",
		RequestFailedException { Code: ErrorCodes.BadRequest } failed => failed.Message,
		_ => transport.State == TransportState.Connected ? "Could not reach the server." : SyncEngine.ConnectionLost,
	};

	private Task<Envelope> RequestAsync<T>(string type, T payload, CancellationToken ct) =>
		transport.RequestAsync(Envelope.Create(type, payload), RequestTimeout, ct);

	/// <summary>A request whose failure becomes an error line in the session on screen.</summary>
	private async Task<bool> TryAsync<T>(string type, T payload)
	{
		CancellationToken token;
		lock (_gate)
		{
			token = _epochCts.Token;
		}

		try
		{
			await RequestAsync(type, payload, token);
			return true;
		}
		catch (Exception ex)
		{
			Current.AddError(Describe(ex));
			Raise();
			return false;
		}
	}

	private void OnSyncChanged() => _ = EnsureOpenAsync();

	private void OnStateChanged(TransportState state)
	{
		if (state != TransportState.Connected)
		{
			// The subscription belonged to the old connection.
			lock (_gate)
			{
				_needsOpen = true;
				_epoch++;

				// Requests in flight lost their reply; deltas that were on their way are lost too.
				_epochCts.Cancel();
				_epochCts.Dispose();
				_epochCts = new CancellationTokenSource();
				foreach (var session in _sessions.Values)
				{
					session.DropLive();
				}
			}

			Raise();
			return;
		}

		_ = EnsureOpenAsync();
	}

	private void OnReceived(Envelope envelope)
	{
		switch (envelope.Type)
		{
			case MessageTypes.ChatSessions:
				lock (_gate)
				{
					Apply(Read<ChatSessionsPayload>(envelope));
				}

				break;
			case MessageTypes.ChatEvent:
				lock (_gate)
				{
					Apply(Read<ChatEvent>(envelope), live: true);
				}

				break;
			default:
				return;
		}

		Raise();
	}

	/// <summary>Whether the chat should be (re)opened now, and on which repo; call under the lock.</summary>
	private bool WantsOpen(out string repo, out int epoch, out CancellationToken token)
	{
		repo = sync.Repo ?? _repo ?? string.Empty;
		epoch = _epoch;
		token = _epochCts.Token;
		return transport.State == TransportState.Connected && repo.Length > 0 && (repo != _repo || _needsOpen);
	}

	/// <summary>
	/// Subscribes to the repo's chat and catches up the sessions whose history was asked for. A failure leaves it to the next trigger
	/// (the connection coming back, the sync engine's next change).
	/// </summary>
	private async Task EnsureOpenAsync()
	{
		if (!Wants(out _, out _, out _))
		{
			return;
		}

		await _opening.WaitAsync();
		try
		{
			if (!Wants(out var repo, out var epoch, out var token))
			{
				return;
			}

			var sessions = Read<ChatSessionsPayload>(await RequestAsync(MessageTypes.ChatOpen, new ChatOpenPayload(repo), token));
			List<ChatSession> catchUp;
			lock (_gate)
			{
				if (repo != _repo)
				{
					_sessions.Clear();
					_listed = [];
					_current = new ChatSession(null);
					_awaitingText = null;
					_since = null;
					_liveCost = null;
					_repo = repo;
				}

				Apply(sessions);
				_needsOpen = _epoch != epoch;

				// A session the server lists as running is loaded too, so that its run is known (Stop) after a reload.
				foreach (var info in sessions.Sessions.Where(i => i.Running))
				{
					GetOrCreate(info.Id).Loaded = true;
				}

				catchUp = [.. _sessions.Values.Where(s => s.Loaded)];
			}

			Raise();
			foreach (var session in catchUp)
			{
				await LoadHistoryAsync(session, token);
			}
		}
		catch (Exception)
		{
			// ponytail: no retry timer; the next connection change or sync change tries again.
		}
		finally
		{
			_opening.Release();
		}

		bool Wants(out string repo, out int epoch, out CancellationToken token)
		{
			lock (_gate)
			{
				return WantsOpen(out repo, out epoch, out token);
			}
		}
	}

	/// <summary>Reads the session's stored events after the last one held, page by page until <c>final</c>.</summary>
	private async Task LoadHistoryAsync(ChatSession session, CancellationToken ct)
	{
		var after = session.Watermark;
		try
		{
			while (true)
			{
				var page = Read<ChatEventsPayload>(await RequestAsync(MessageTypes.ChatHistory, new ChatHistoryPayload(session.Id!, after), ct));
				lock (_gate)
				{
					foreach (var e in page.Events)
					{
						Add(session, e, live: false);
					}

					Refresh();
				}

				Raise();
				if (page.Final || page.Events.Count == 0)
				{
					return;
				}

				after = Math.Max(after, page.Events.Max(e => e.Seq));
			}
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			// The next catch-up asks again; the line tells the user the history may be incomplete.
			session.AddError($"Could not load the history: {Describe(ex)}");
			Raise();
		}
		catch (Exception)
		{
			// The connection changed under the request: the catch-up after it asks again.
		}
	}

	/// <summary>The session of an event or a listing; call under the lock.</summary>
	private ChatSession GetOrCreate(string id)
	{
		if (!_sessions.TryGetValue(id, out var session))
		{
			_sessions[id] = session = new ChatSession(id);
		}

		return session;
	}

	private void Apply(ChatSessionsPayload payload)
	{
		if (!string.Equals(payload.Repo, _repo, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		_listed = payload.Sessions;
		foreach (var info in payload.Sessions)
		{
			if (_sessions.TryGetValue(info.Id, out var session))
			{
				session.SetListed(info);
			}
		}

		Refresh();
	}

	private void Apply(ChatEvent e, bool live)
	{
		if (!_sessions.TryGetValue(e.SessionId, out var session))
		{
			session = GetOrCreate(e.SessionId);

			// The prompt of the message this page just sent: its session is the draft on screen.
			if (live && e.Kind == ChatEventKinds.Prompt && _current.IsDraft && _awaitingText is { } sent && e.Text is { } prompt && sent.StartsWith(prompt, StringComparison.Ordinal))
			{
				_current = session;
				session.Loaded = true;
			}
		}

		Add(session, e, live);
		Refresh();
	}

	private void Add(ChatSession session, ChatEvent e, bool live)
	{
		if (e.Kind == ChatEventKinds.Text)
		{
			session.AddText(e);
		}
		else if (session.AddStored(e, live) && e.Kind == ChatEventKinds.Result && e.CostUsd is { } cost && live)
		{
			_liveCost = cost;
		}
	}

	private bool IsRunning() =>
		_sessions.Values.Any(s => s.Running) || _listed.Any(s => s.Running && !_sessions.ContainsKey(s.Id));

	/// <summary>Starts or stops the elapsed-time clock when the repo's run starts or ends; call under the lock.</summary>
	private void Refresh()
	{
		var running = IsRunning();
		if (running && _since is null)
		{
			_since = time.GetUtcNow();
		}
		else if (!running)
		{
			_since = null;
		}
	}

	private void Raise() => Changed?.Invoke();
}
