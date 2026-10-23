using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Client.Chat;

/// <summary>
/// The browser side of the chat: opens the open folder's chat (or, with <see cref="ViewAsync"/>, another project's, read-only) on the server (<c>chat.open</c>, once the folder is synced and the
/// connection is up, and again after every reconnect), keeps a <see cref="ChatSession"/> per session it has seen and sends, stops and
/// approves. State is keyed on the events' own <c>sessionId</c>/<c>runId</c>: the sender gets the <c>prompt</c> of its run before the
/// <c>chat.started</c> reply, and another tab's runs arrive the same way. After a reconnect the stored events missed are read with
/// <c>chat.history</c> for every session whose history was asked for; replays are dropped by <c>seq</c>.
/// </summary>
public sealed class ChatEngine(ITransport transport, SyncEngine sync, TimeProvider time)
{
	/// <summary>The error line of a message typed while another project is viewed.</summary>
	public const string ReadOnlyNote = "Read-only: open this folder on its computer to chat.";

	/// <summary>How long a request waits for its reply (a reconnect cancels it sooner).</summary>
	public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	/// <summary>How long <b>Check now</b> waits: the server runs <c>claude mcp list</c> and <c>claude plugin list</c> side by side, up to 60 s each.</summary>
	public static readonly TimeSpan ToolsCheckTimeout = TimeSpan.FromSeconds(90);

	private const string NoFolder = "Open a folder first.";

	private const string TooLong = "The message is too long; shorten it.";

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
	private bool _stopping;

	/// <summary>The repo shown read-only instead of the open folder's (another computer's project), or null.</summary>
	private string? _viewing;

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

	/// <summary>Gets a value indicating whether Stop was pressed and the run has not ended yet (the cancel request failing ends it too).</summary>
	public bool Stopping
	{
		get
		{
			lock (_gate)
			{
				return _stopping && IsRunning();
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

	/// <summary>Gets the repo the chat sends to (the open folder's), or null without one.</summary>
	public string? Repo
	{
		get
		{
			lock (_gate)
			{
				return _repo ?? sync.Repo;
			}
		}
	}

	/// <summary>Gets the repo shown read-only (another computer's project on the server), or null for the open folder's chat.</summary>
	public string? Viewing
	{
		get
		{
			lock (_gate)
			{
				return _viewing;
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

	/// <summary>
	/// Shows another project's chat read-only (<paramref name="repo"/>), or the open folder's again (null). <c>chat.open</c> moves this
	/// connection's subscription there, so the folder's own chat gets no pushes meanwhile; its history catches up on return.
	/// </summary>
	public async Task ViewAsync(string? repo)
	{
		lock (_gate)
		{
			_viewing = repo;
			if (repo is null && sync.Repo is null)
			{
				// Back without a folder of its own: nothing to show.
				Reset(null);
			}
		}

		Raise();
		await EnsureOpenAsync();
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

	/// <summary>
	/// Sends a message into the session on screen (a new session when it is a draft). A refusal (<c>busy</c>, too long, ...) becomes an error
	/// line of the session; so does a message over <see cref="ChatLimits.MaxSendBytes"/> once encoded, which is not sent (the hub would drop the connection).
	/// </summary>
	/// <param name="text">The message.</param>
	/// <param name="attachments">Ids of uploaded files (<see cref="ChatAttachments.ReadyIds"/>).</param>
	/// <returns>Whether the run started (false: the refusal is an error line of the session).</returns>
	public async Task<bool> SendAsync(string text, IReadOnlyList<string>? attachments = null)
	{
		if (IsReadOnly())
		{
			Current.AddError(ReadOnlyNote);
			Raise();
			return false;
		}

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
		var send = repo is null ? null : Envelope.Create(MessageTypes.ChatSend, new ChatSendPayload(repo, session.Id, text, attachments is { Count: > 0 } ? attachments : null));
		if (send is null || JsonSerializer.SerializeToUtf8Bytes(send, JsonSerializerOptions.Web).Length > ChatLimits.MaxSendBytes)
		{
			lock (_gate)
			{
				_awaitingText = null;
			}

			session.AddError(send is null ? NoFolder : TooLong);
			Raise();
			return false;
		}

		var ok = false;
		try
		{
			var started = Read<ChatStartedPayload>(await transport.RequestAsync(send, RequestTimeout, token));
			lock (_gate)
			{
				if (session == _current && session.IsDraft)
				{
					_current = GetOrCreate(started.SessionId);
					_current.Loaded = true;
				}

				_awaitingText = null;
			}

			ok = true;
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
		return ok;
	}

	/// <summary>
	/// <b>Stop</b>: cancels the run in progress, shows <see cref="Stopping"/> at once and sends one cancel however often it is called. Without a known
	/// run id (the history of the running session is still loading) the folder's run is named by the repo.
	/// </summary>
	public async Task StopAsync()
	{
		string? runId;
		string? repo;
		lock (_gate)
		{
			repo = _repo ?? sync.Repo;
			runId = _current.ActiveRunId ?? _sessions.Values.Select(s => s.ActiveRunId).FirstOrDefault(id => id is not null);
			if (IsReadOnlyLocked() || _stopping || !IsRunning() || (runId is null && repo is null))
			{
				return;
			}

			_stopping = true;
		}

		Raise();
		if (!await TryAsync(MessageTypes.ChatCancel, new ChatCancelPayload(runId, runId is null ? repo : null)))
		{
			lock (_gate)
			{
				_stopping = false;
			}

			Raise();
		}
	}

	/// <summary>Answers a permission request of the session on screen (<see cref="ChatDecisions"/>); the card closes when the server's <c>permissionResolved</c> arrives.</summary>
	/// <param name="requestId">The card's <see cref="ChatApproval.RequestId"/>.</param>
	/// <param name="decision">One of <see cref="ChatDecisions"/>.</param>
	/// <returns>Whether the answer was sent (false: the card is gone, or the request failed, which shows as an error line).</returns>
	public async Task<bool> ApproveAsync(string requestId, string decision)
	{
		if (IsReadOnly())
		{
			return false;
		}

		ChatApproval? card;
		lock (_gate)
		{
			card = _sessions.Values.SelectMany(s => s.Approvals).FirstOrDefault(a => a.RequestId == requestId);
		}

		return card is not null && await TryAsync(MessageTypes.ChatApprove, new ChatApprovePayload(card.RunId, requestId, decision));
	}

	/// <summary>
	/// The open folder's Claude tools as last recorded (<c>agent.tools.get</c>), or after a fresh check (<c>agent.tools.check</c>, which may take
	/// up to <see cref="ToolsCheckTimeout"/>; a failed check answers the old rows with <c>error</c> set).
	/// </summary>
	/// <exception cref="InvalidOperationException">No folder is open.</exception>
	public async Task<ClaudeToolsPayload> ToolsAsync(bool check)
	{
		string? repo;
		CancellationToken token;
		lock (_gate)
		{
			if (IsReadOnlyLocked())
			{
				throw new InvalidOperationException(ReadOnlyNote);
			}

			token = _epochCts.Token;
			repo = _repo ?? sync.Repo;
		}

		var request = Envelope.Create(check ? MessageTypes.AgentToolsCheck : MessageTypes.AgentToolsGet, new ClaudeToolsRequest(repo ?? throw new InvalidOperationException(NoFolder)));
		return Read<ClaudeToolsPayload>(await transport.RequestAsync(request, check ? ToolsCheckTimeout : RequestTimeout, token));
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	/// <summary>
	/// Whether the chat on screen is not the open folder's: another project is viewed, or the subscription still sits on one (after
	/// <see cref="ViewAsync"/> back, until the folder's own <c>chat.open</c> succeeded). Nothing may write to it then.
	/// </summary>
	private bool IsReadOnly()
	{
		lock (_gate)
		{
			return IsReadOnlyLocked();
		}
	}

	private bool IsReadOnlyLocked() =>
		_viewing is not null || (_repo is { } open && sync.Repo is { } local && !string.Equals(open, local, StringComparison.OrdinalIgnoreCase));

	private string Describe(Exception ex) => ex switch
	{
		RequestFailedException { Code: ErrorCodes.Busy } => "Claude is already working in this folder; wait for it or stop it.",
		RequestFailedException { Code: ErrorCodes.TooLarge } => TooLong,
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
		repo = _viewing ?? sync.Repo ?? _repo ?? string.Empty;
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
					Reset(repo);
				}

				Apply(sessions);
				_needsOpen = _epoch != epoch;

				// A session the server lists as running is loaded too, so that its run is known (Stop) after a reload.
				foreach (var info in sessions.Sessions.Where(i => i.Running))
				{
					var running = GetOrCreate(info.Id);
					running.Loaded = true;

					// Running at once (Stop works), before its history says which run it is.
					running.SetListed(info);
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

	/// <summary>Forgets the shown repo's sessions and run state; call under the lock.</summary>
	private void Reset(string? repo)
	{
		_sessions.Clear();
		_listed = [];
		_current = new ChatSession(null);
		_awaitingText = null;
		_since = null;
		_liveCost = null;
		_stopping = false;
		_repo = repo;
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
			if (live && e.Kind == ChatEventKinds.Prompt && _current.IsDraft && _awaitingText is { } sent && e.Text is { } prompt && (prompt.Length == 0 ? sent.Length == 0 : sent.StartsWith(prompt, StringComparison.Ordinal)))
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
			_stopping = false;
		}
	}

	private void Raise() => Changed?.Invoke();
}
