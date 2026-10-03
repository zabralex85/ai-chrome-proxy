using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

/// <summary>Runs through <see cref="ChatHandler"/> and <see cref="ChatService"/> with a scripted <see cref="FakeAgentRunner"/>.</summary>
public sealed class ChatServiceTests : IDisposable
{
	private const string Repo = "repo";
	private const string Init = """{"type":"system","subtype":"init","session_id":"sess-1","cwd":"C:/mirror/repo"}""";
	private const string Delta = """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""";
	private const string Message = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello there."}]}}""";
	private const string ToolUse = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""";
	private const string ToolResult = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"a.txt","is_error":false}]}}""";
	private const string Result = """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"result":"Hello there.","total_cost_usd":0.0123,"session_id":"sess-1"}""";

	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FakeTimeProvider _time = new();
	private readonly MemoryChatStore _store;
	private readonly MemoryProjectStore _projects = new();
	private readonly FakeAgentRunner _runner = new();
	private readonly ListLogger<ChatService> _logger = new();
	private readonly ChatService _service;
	private readonly EnvelopeRouter _router;

	public ChatServiceTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, Repo));
		Directory.CreateDirectory(Path.Combine(_root, "other"));
		_store = new MemoryChatStore(_time);
		var mirror = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
		_service = new ChatService(_store, mirror, _projects, _runner, _time, _logger);
		_router = new EnvelopeRouter([.. ChatHandler.Types.Select(t => new ChatHandler(t, _service))]);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose()
	{
		_service.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public async Task Send_StartedAtOnce_EventsPushedToRepoSubscribers_StoredWithoutDeltas()
	{
		var sender = new Client("c1");
		var watcher = new Client("c2");
		var elsewhere = new Client("c3");
		await OpenAsync(sender, Repo);
		await OpenAsync(watcher, Repo);
		await OpenAsync(elsewhere, "other");

		var started = await SendAsync(sender, Repo, null, "Say hello");
		var process = await _runner.NextAsync();
		Assert.True(Assert.Single(await SessionsAsync(watcher, Repo)).Running);

		process.Write(Init, Delta, Message, ToolUse, ToolResult, Result);
		process.Exit();
		await watcher.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		await sender.WaitAsync(e => e.Kind == ChatEventKinds.Result);

		Assert.Equal([ChatEventKinds.Prompt, ChatEventKinds.Text, ChatEventKinds.Message, ChatEventKinds.Tool, ChatEventKinds.ToolResult, ChatEventKinds.Result], watcher.Events.Select(e => e.Kind));
		Assert.Equal(watcher.Events, sender.Events);
		Assert.Empty(elsewhere.Events);
		Assert.All(watcher.Events, e => Assert.Equal((started.SessionId, started.RunId), (e.SessionId, e.RunId)));
		Assert.Equal(0, watcher.Events[1].Seq);

		var (stored, final) = _store.Read(started.SessionId, 0, ChatLimits.MaxEventBytes);
		Assert.True(final);
		Assert.Equal(watcher.Events.Where(e => e.Kind != ChatEventKinds.Text), stored);
		Assert.Equal([1L, 2, 3, 4, 5], stored.Select(e => e.Seq));
		Assert.Equal(new ChatEvent(started.SessionId, started.RunId, 1, ChatEventKinds.Prompt, Text: "Say hello"), stored[0]);
		Assert.Equal("Hello there.", stored[1].Text);
		Assert.Equal(new ChatEvent(started.SessionId, started.RunId, 5, ChatEventKinds.Result, Ok: true, CostUsd: 0.0123m, DurationMs: 1234), stored[4]);

		var session = Assert.Single(await SessionsAsync(sender, Repo));
		Assert.Equal((started.SessionId, "Say hello", false), (session.Id, session.Title, session.Running));
		Assert.Equal(new AgentRun(Path.Combine(_root, Repo), "Say hello"), process.Run with { AllowedTools = null });
		Assert.True(process.Disposed);
		Assert.False(process.Killed);
	}

	[Fact]
	public async Task Send_PushesSessionsToSubscribers_WhenRunStartsAndEnds()
	{
		var watcher = new Client("c2");
		await OpenAsync(watcher, Repo);

		var started = await SendAsync(new Client("c1"), Repo, null, "Hi");
		var process = await _runner.NextAsync();
		await watcher.WaitForSessionsAsync(s => s.Any(x => x.Id == started.SessionId && x.Running));
		process.Write(Result);
		process.Exit();

		await watcher.WaitForSessionsAsync(s => s.Any(x => x.Id == started.SessionId && !x.Running));
	}

	[Fact]
	public async Task SecondSend_WhileRunning_Busy_OtherRepoNot()
	{
		var client = new Client("c1");
		var first = await SendAsync(client, Repo, null, "One");
		var process = await _runner.NextAsync();

		Assert.Equal(ErrorCodes.Busy, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, first.SessionId, "Two")));
		Assert.Equal(ErrorCodes.Busy, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, "Two")));
		Assert.Single(_store.ListSessions(Repo));
		Assert.Equal([ChatEventKinds.Prompt], _store.Read(first.SessionId, 0, int.MaxValue).Events.Select(e => e.Kind));
		await SendAsync(new Client("c2"), "other", null, "Elsewhere");

		process.Write(Result);
		process.Exit();
		await client.WaitAsync(e => e.Kind == ChatEventKinds.Result && e.SessionId == first.SessionId);
		await SendAsync(client, Repo, first.SessionId, "Two");
	}

	[Fact]
	public async Task Cancel_KillsProcess_ResultCancelled_Echoed()
	{
		var client = new Client("c1");
		var started = await SendAsync(client, Repo, null, "Long job");
		var process = await _runner.NextAsync();
		process.Write(Init, Message);
		await client.WaitAsync(e => e.Kind == ChatEventKinds.Message);

		var echo = await RouteAsync(client, MessageTypes.ChatCancel, new ChatCancelPayload(started.RunId));

		Assert.Equal(MessageTypes.ChatCancel, echo.Type);
		Assert.Equal(started.RunId, echo.Payload.Deserialize<ChatCancelPayload>(JsonSerializerOptions.Web)!.RunId);
		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.True(process.Killed);
		Assert.Equal((false, "Cancelled."), (result.Ok, result.Error));
		Assert.Equal(result, _store.Read(started.SessionId, 0, ChatLimits.MaxEventBytes).Events[^1]);
	}

	[Fact]
	public async Task Cancel_UnknownRun_EchoedAnyway()
	{
		var echo = await RouteAsync(new Client("c1"), MessageTypes.ChatCancel, new ChatCancelPayload("nope"));

		Assert.Equal(MessageTypes.ChatCancel, echo.Type);
	}

	[Fact]
	public async Task NoOutputForIdleTimeout_Killed_IdleError_OutputResetsTheTimer()
	{
		var client = new Client("c1");
		await SendAsync(client, Repo, null, "Hang");
		var process = await _runner.NextAsync();

		_time.Advance(TimeSpan.FromMinutes(9));
		process.Write(Message);
		await client.WaitAsync(e => e.Kind == ChatEventKinds.Message);
		_time.Advance(TimeSpan.FromMinutes(9));
		Assert.False(process.Killed);

		_time.Advance(TimeSpan.FromMinutes(1));

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.True(process.Killed);
		Assert.False(result.Ok);
		Assert.Contains("10 minutes", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StartFailure_ResultWithTheRunnersAdvice_RunEnds()
	{
		const string advice = "`claude` was not found on PATH for the service account; set Agent:Command.";
		_runner.StartError = new FileNotFoundException(advice);
		var client = new Client("c1");

		var started = await SendAsync(client, Repo, null, "Hi");

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.Equal((false, advice), (result.Ok, result.Error));
		Assert.False(Assert.Single(await SessionsAsync(client, Repo)).Running);
		Assert.Equal(started.SessionId, result.SessionId);
	}

	[Fact]
	public async Task UnexpectedStartFailure_GenericError_Logged()
	{
		_runner.StartError = new NotSupportedException("secret detail");
		var client = new Client("c1");

		await SendAsync(client, Repo, null, "Hi");

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.False(result.Ok);
		Assert.DoesNotContain("secret", result.Error, StringComparison.Ordinal);
		Assert.Contains(_logger.Messages, m => m.Contains("failed", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ExitWithoutResult_StderrTailIsTheError()
	{
		var client = new Client("c1");
		await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();
		process.Stderr = "Invalid API key · Please run /login";

		process.Exit(1);

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.Equal((false, "Invalid API key · Please run /login"), (result.Ok, result.Error));
	}

	[Fact]
	public async Task ExitWithoutResultOrStderr_ExitCodeInTheError()
	{
		var client = new Client("c1");
		await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		process.Exit(3);

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.False(result.Ok);
		Assert.Contains("3", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task NextTurn_ResumesClaudeSession_WithProjectSettings()
	{
		_projects.SaveSettings(Repo, new ProjectSettings { AgentPermissions = "all", AgentModel = "opus", AgentAllowedTools = ["Bash(ls)"] });
		var client = new Client("c1");
		var first = await SendAsync(client, Repo, null, "One");
		var process = await _runner.NextAsync();
		Assert.Null(process.Run.ResumeId);
		process.Write(Init, Result);
		process.Exit();
		await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.Equal("sess-1", _store.GetClaudeSession(first.SessionId));

		var second = await SendAsync(client, Repo, first.SessionId, "Two");
		var next = await _runner.NextAsync();

		Assert.Equal(first.SessionId, second.SessionId);
		Assert.NotEqual(first.RunId, second.RunId);
		Assert.Equal(new AgentRun(Path.Combine(_root, Repo), "Two", "sess-1", "all", "opus"), next.Run with { AllowedTools = null });
		Assert.Equal(["Bash(ls)"], next.Run.AllowedTools!);
		Assert.Null(next.Run.ApprovalUrl);
	}

	[Fact]
	public async Task History_PagedWithinTheLimit_UntilFinal()
	{
		var session = _store.CreateSession(Repo, "t");
		_store.Append(session.Id, [.. Enumerable.Range(0, 30).Select(i => new ChatEvent(string.Empty, "r", 0, ChatEventKinds.Message, new string('x', 2000)))]);
		var client = new Client("c1");

		var seqs = new List<long>();
		var after = 0L;
		var pages = 0;
		while (true)
		{
			var reply = await RouteAsync(client, MessageTypes.ChatHistory, new ChatHistoryPayload(session.Id, after));
			Assert.Equal(MessageTypes.ChatEvents, reply.Type);
			Assert.True(JsonSerializer.SerializeToUtf8Bytes(reply.Payload, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes);
			var page = reply.Payload.Deserialize<ChatEventsPayload>(JsonSerializerOptions.Web)!;
			Assert.Equal(session.Id, page.SessionId);
			seqs.AddRange(page.Events.Select(e => e.Seq));
			pages++;
			if (page.Final)
			{
				break;
			}

			after = page.Events[^1].Seq;
		}

		Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), seqs);
		Assert.True(pages > 1);
	}

	[Fact]
	public async Task SenderDisconnects_RunGoesOn_OthersStillGetEvents()
	{
		var sender = new Client("c1");
		var watcher = new Client("c2");
		await OpenAsync(watcher, Repo);
		await SendAsync(sender, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		_service.Unsubscribe("c1");
		process.Write(Message, Result);
		process.Exit();

		await watcher.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.False(process.Killed);
		Assert.Equal([ChatEventKinds.Prompt], sender.Events.Select(e => e.Kind));
	}

	[Fact]
	public async Task SubscriberSendFails_OthersStillServed()
	{
		var broken = new Client("c1") { Fails = true };
		var watcher = new Client("c2");
		await OpenAsync(broken, Repo);
		await OpenAsync(watcher, Repo);
		await SendAsync(watcher, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		process.Write(Message, Result);
		process.Exit();

		await watcher.WaitAsync(e => e.Kind == ChatEventKinds.Result);
	}

	[Fact]
	public async Task RepoName_Sanitized()
	{
		Directory.CreateDirectory(Path.Combine(_root, "My_Repo"));
		var client = new Client("c1");

		var reply = await RouteAsync(client, MessageTypes.ChatOpen, new ChatOpenPayload("My Repo"));
		await SendAsync(client, "My Repo", null, "Hi");
		var process = await _runner.NextAsync();

		Assert.Equal("My_Repo", reply.Payload.Deserialize<ChatSessionsPayload>(JsonSerializerOptions.Web)!.Repo);
		Assert.Equal(Path.Combine(_root, "My_Repo"), process.Run.RepoFolder);
		Assert.Single(_store.ListSessions("My_Repo"));
	}

	[Fact]
	public async Task NewSession_TitleIsTheFirst60CharactersOnOneLine()
	{
		var client = new Client("c1");

		await SendAsync(client, Repo, null, "  First line\r\nsecond " + new string('y', 100));

		Assert.Equal(("First line second " + new string('y', 100))[..60], Assert.Single(_store.ListSessions(Repo)).Title);
	}

	[Fact]
	public async Task InvalidRequests_Refused()
	{
		var client = new Client("c1");
		var other = _store.CreateSession("other", "t");

		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatOpen, new ChatOpenPayload(" ")));
		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(string.Empty, null, "Hi")));
		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, " ")));
		Assert.Equal(ErrorCodes.TooLarge, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, new string('a', ChatLimits.MaxTextChars + 1))));
		Assert.Equal(ErrorCodes.NotFound, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, "missing", "Hi")));
		Assert.Equal(ErrorCodes.NotFound, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, other.Id, "Hi")));
		Assert.Equal(ErrorCodes.NotFound, await ErrorAsync(client, MessageTypes.ChatHistory, new ChatHistoryPayload("missing", 0)));
		Assert.Equal(ErrorCodes.NotFound, await ErrorAsync(client, MessageTypes.ChatApprove, new ChatApprovePayload("r", "q", ChatDecisions.Allow)));
		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatSend, (object?)null));
		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatHistory, new { sessionId = 5 }));
		Assert.Empty(_store.ListSessions(Repo));
	}

	[Fact]
	public async Task MaxLengthText_Accepted()
	{
		await SendAsync(new Client("c1"), Repo, null, new string('a', ChatLimits.MaxTextChars));
	}

	[Fact]
	public async Task MirrorFolderMissing_BadRequest_WithAdvice()
	{
		var client = new Client("c1");

		var reply = await RouteAsync(client, MessageTypes.ChatSend, new ChatSendPayload("never-synced", null, "Hi"));

		var error = reply.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web)!;
		Assert.Equal((ErrorCodes.BadRequest, "Open the folder and let it sync first."), (error.Code, error.Message));
	}

	[Fact]
	public async Task LongEscapedMessage_SplitIntoEventsWithinTheLimit()
	{
		var client = new Client("c1");
		var started = await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();
		var text = string.Concat(Enumerable.Repeat("\"<>&", 6000));

		process.Write(JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text } } } }), Result);
		process.Exit();

		await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		var messages = client.Events.Where(e => e.Kind == ChatEventKinds.Message).ToList();
		Assert.True(messages.Count > 1);
		Assert.Equal(text, string.Concat(messages.Select(e => e.Text)));
		Assert.All(client.Events, e => Assert.True(JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes));
		Assert.Equal(client.Events, _store.Read(started.SessionId, 0, int.MaxValue).Events);

		// Near-limit parts: every history page still fits the limit literally.
		var after = 0L;
		while (true)
		{
			var reply = await RouteAsync(client, MessageTypes.ChatHistory, new ChatHistoryPayload(started.SessionId, after));
			Assert.True(JsonSerializer.SerializeToUtf8Bytes(reply.Payload, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes);
			var page = reply.Payload.Deserialize<ChatEventsPayload>(JsonSerializerOptions.Web)!;
			if (page.Final)
			{
				break;
			}

			after = page.Events[^1].Seq;
		}
	}

	[Fact]
	public async Task Dispose_StopsRunningAgents()
	{
		var client = new Client("c1");
		var started = await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		_service.Dispose();

		Assert.True(process.Killed);
		var last = _store.Read(started.SessionId, 0, int.MaxValue).Events[^1];
		Assert.Equal((ChatEventKinds.Result, false), (last.Kind, last.Ok));
	}

	[Fact]
	public async Task LongestPromptOfEscapes_StoredInPartsWithinTheLimit_BeforeTheRunsEvents()
	{
		var client = new Client("c1");
		var text = new string('"', ChatLimits.MaxTextChars);

		var started = await SendAsync(client, Repo, null, text);
		var process = await _runner.NextAsync();
		process.Write(Message, Result);
		process.Exit();
		await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);

		var stored = _store.Read(started.SessionId, 0, int.MaxValue).Events;
		var prompts = stored.TakeWhile(e => e.Kind == ChatEventKinds.Prompt).ToList();
		Assert.True(prompts.Count > 1);
		Assert.Equal(text, string.Concat(prompts.Select(e => e.Text)));
		Assert.Equal([ChatEventKinds.Message, ChatEventKinds.Result], stored.Skip(prompts.Count).Select(e => e.Kind));
		Assert.All(stored, e => Assert.True(JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes));
		Assert.Equal(stored, client.Events);
	}

	[Fact]
	public async Task InvalidIdleTimeout_StillOneResult_RepoFreed()
	{
		_runner.IdleTimeout = TimeSpan.FromMilliseconds(-5);
		var client = new Client("c1");

		var started = await SendAsync(client, Repo, null, "Hi");

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.False(result.Ok);
		Assert.Single(_store.Read(started.SessionId, 0, int.MaxValue).Events, e => e.Kind == ChatEventKinds.Result);
		Assert.Contains(_logger.Messages, m => m.Contains("failed", StringComparison.OrdinalIgnoreCase));
		_runner.IdleTimeout = TimeSpan.FromMinutes(10);
		await SendAsync(client, Repo, started.SessionId, "Again");
	}

	[Fact]
	public async Task ResultButNoExit_KilledAfterTheGrace_ResultKept()
	{
		var client = new Client("c1");
		await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		process.Write(Result);

		var result = await AdvanceUntilAsync(client, e => e.Kind == ChatEventKinds.Result);
		Assert.True(result.Ok);
		Assert.True(process.Killed);
	}

	[Fact]
	public async Task OutputClosedButNoExit_KilledAfterTheGrace_Failed()
	{
		var client = new Client("c1");
		await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		process.CloseOutput();

		var result = await AdvanceUntilAsync(client, e => e.Kind == ChatEventKinds.Result);
		Assert.Equal((false, "Claude's output ended but it did not exit."), (result.Ok, result.Error));
		Assert.True(process.Killed);
	}

	[Fact]
	public async Task CancelBeforeTheProcessStarts_Cancelled_NothingStarted()
	{
		_runner.StartGate = new TaskCompletionSource().Task;
		var client = new Client("c1");
		var started = await SendAsync(client, Repo, null, "Hi");

		await RouteAsync(client, MessageTypes.ChatCancel, new ChatCancelPayload(started.RunId));

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.Equal((false, "Cancelled."), (result.Ok, result.Error));
		Assert.False(_runner.Started.Reader.TryRead(out _));
	}

	[Fact]
	public async Task StoreFailsMidRun_ProcessKilled_RepoFreed()
	{
		var client = new Client("c1");
		var started = await SendAsync(client, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		_store.AppendError = new IOException("disk full");
		process.Write(Message);

		var result = await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.False(result.Ok);
		Assert.True(process.Killed);
		_store.AppendError = null;
		await SendAsync(client, Repo, started.SessionId, "Again");
	}

	[Fact]
	public async Task ConcurrentSends_ExactlyOneBusy()
	{
		var client = new Client("c1");

		var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => RouteAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, "Hi")), Ct)));

		Assert.Single(replies, r => r.Type == MessageTypes.ChatStarted);
		Assert.Single(replies, r => r.Type == MessageTypes.Error && r.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web)!.Code == ErrorCodes.Busy);
		Assert.Single(_store.ListSessions(Repo));
	}

	[Fact]
	public async Task StalledSubscriber_DroppedAndAbortedAtTheLimit_OthersServed()
	{
		var stalled = new Client("c1") { Stalled = new TaskCompletionSource().Task };
		var watcher = new Client("c2");
		await OpenAsync(stalled, Repo);
		await OpenAsync(watcher, Repo);
		await SendAsync(watcher, Repo, null, "Hi");
		var process = await _runner.NextAsync();

		process.Write([.. Enumerable.Repeat(Delta, ChatService.MaxPendingPushes + 10)]);
		process.Write(Result);
		process.Exit();

		await watcher.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.Equal(1, stalled.Aborts);
		Assert.Equal(0, watcher.Aborts);
		Assert.Equal(ChatService.MaxPendingPushes + 10, watcher.Events.Count(e => e.Kind == ChatEventKinds.Text));
		Assert.Equal(1, stalled.Pushes);
	}

	private async Task<ChatEvent> AdvanceUntilAsync(Client client, Func<ChatEvent, bool> match)
	{
		for (var i = 0; i < 100 && !client.Events.Any(match); i++)
		{
			_time.Advance(TimeSpan.FromSeconds(1));
			await Task.Delay(20, Ct);
		}

		return await client.WaitAsync(match);
	}

	private async Task<Envelope> RouteAsync(Client client, string type, object? payload) =>
		(await _router.RouteAsync(Envelope.Create(type, payload), client.Context, Ct))!;

	private async Task<string> ErrorAsync(Client client, string type, object? payload)
	{
		var reply = await RouteAsync(client, type, payload);
		Assert.Equal(MessageTypes.Error, reply.Type);
		return reply.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web)!.Code;
	}

	private async Task OpenAsync(Client client, string repo) => await SessionsAsync(client, repo);

	private async Task<IReadOnlyList<ChatSessionInfo>> SessionsAsync(Client client, string repo)
	{
		var reply = await RouteAsync(client, MessageTypes.ChatOpen, new ChatOpenPayload(repo));
		Assert.Equal(MessageTypes.ChatSessions, reply.Type);
		return reply.Payload.Deserialize<ChatSessionsPayload>(JsonSerializerOptions.Web)!.Sessions;
	}

	private async Task<ChatStartedPayload> SendAsync(Client client, string repo, string? sessionId, string text)
	{
		var reply = await RouteAsync(client, MessageTypes.ChatSend, new ChatSendPayload(repo, sessionId, text));
		Assert.Equal(MessageTypes.ChatStarted, reply.Type);
		return reply.Payload.Deserialize<ChatStartedPayload>(JsonSerializerOptions.Web)!;
	}

	/// <summary>A connection: records what is pushed to it.</summary>
	private sealed class Client(string id)
	{
		private readonly List<Envelope> _pushed = [];
		private int _aborts;

		public bool Fails { get; init; }

		/// <summary>When set, every send waits for it (a connection that stopped reading).</summary>
		public Task? Stalled { get; init; }

		public int Aborts => Volatile.Read(ref _aborts);

		public EnvelopeContext Context => new(
			id,
			null,
			(e, _) =>
			{
				if (Fails)
				{
					throw new IOException("gone");
				}

				lock (_pushed)
				{
					_pushed.Add(e);
				}

				return Stalled ?? Task.CompletedTask;
			},
			() => Interlocked.Increment(ref _aborts));

		/// <summary>Gets how many envelopes reached the send function (stalled or not).</summary>
		public int Pushes
		{
			get
			{
				lock (_pushed)
				{
					return _pushed.Count;
				}
			}
		}

		public IReadOnlyList<ChatEvent> Events => [.. Pushed(MessageTypes.ChatEvent).Select(p => p.Deserialize<ChatEvent>(JsonSerializerOptions.Web)!)];

		public async Task<ChatEvent> WaitAsync(Func<ChatEvent, bool> match)
		{
			await Until(() => Events.Any(match));
			return Events.First(match);
		}

		public Task WaitForSessionsAsync(Func<IReadOnlyList<ChatSessionInfo>, bool> match) =>
			Until(() => Pushed(MessageTypes.ChatSessions).Any(p => match(p.Deserialize<ChatSessionsPayload>(JsonSerializerOptions.Web)!.Sessions)));

		private static async Task Until(Func<bool> condition)
		{
			for (var i = 0; i < 1000 && !condition(); i++)
			{
				await Task.Delay(10, CancellationToken.None);
			}

			Assert.True(condition());
		}

		private List<JsonElement> Pushed(string type)
		{
			lock (_pushed)
			{
				return [.. _pushed.Where(e => e.Type == type).Select(e => e.Payload)];
			}
		}
	}
}
