using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Tests.Application;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Client;

/// <summary>The chat engine against <see cref="LoopbackServer"/> (the real <c>ChatService</c>) with a scripted agent.</summary>
public sealed class ChatEngineTests : IDisposable
{
	private const string Init = """{"type":"system","subtype":"init","session_id":"sess-1","cwd":"C:/mirror/My_Repo"}""";
	private const string Delta = """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""";
	private const string Message = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello there."}]}}""";
	private const string Message2 = """{"type":"assistant","message":{"content":[{"type":"text","text":"All done."}]}}""";
	private const string ToolUse = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""";
	private const string ToolResult = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"a.txt","is_error":false}]}}""";
	private const string Result = """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"result":"Hello there.","total_cost_usd":0.0123,"session_id":"sess-1"}""";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly FakeTimeProvider _time = new();
	private readonly SyncEngine _sync;
	private readonly ChatEngine _chat;

	public ChatEngineTests()
	{
		_sync = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		_chat = new ChatEngine(_server.Transport, _sync, _time);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task NotOpenedUntilTheFolderIsSynced_ThenOpensAndListsSessions()
	{
		await _chat.InitializeAsync();
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.ChatOpen);

		await SyncedAsync();
		await UntilAsync(() => _server.Transport.Sent.Any(e => e.Type == MessageTypes.ChatOpen));

		Assert.Empty(_chat.Sessions);
		Assert.False(_chat.Running);
	}

	[Fact]
	public async Task StreamedAnswer_DeltasThenMessageReplacesThem_ResultSetsCost()
	{
		var process = await StartAsync("hi");
		process.Write(Init, Delta);
		await UntilAsync(() => _chat.Current.Items is [_, { Streaming: true }]);
		Assert.Equal(new ChatItem(ChatItemKind.Assistant, _chat.Current.ActiveRunId!, "Hel", Streaming: true), _chat.Current.Items[1]);
		Assert.True(_chat.Running);

		process.Write(Message, ToolUse, ToolResult, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);

		var items = _chat.Current.Items;
		Assert.Equal(["hi", "Hello there.", "ls"], items.Select(i => i.Text));
		Assert.Equal([ChatItemKind.User, ChatItemKind.Assistant, ChatItemKind.Tool], items.Select(i => i.Kind));
		Assert.Equal(("Bash", "a.txt", false), (items[2].Name, items[2].Result, items[2].IsError));
		Assert.Equal(0.0123m, _chat.LastCost);
		Assert.Null(_chat.Elapsed);
		Assert.Equal("hi", _chat.Current.Title);
	}

	[Fact]
	public async Task EventsAreKeyedOnTheirSession_PromptBeforeStartedReply_NewSessionAdopted()
	{
		await ReadyAsync();
		var seen = new List<(string? Id, int Items)>();
		_chat.Changed += () => seen.Add((_chat.Current.Id, _chat.Current.Items.Count));
		await _chat.SendAsync("first question");
		var process = await _server.Agent.NextAsync();

		// The prompt push (one row) was handled before the started reply, and already belonged to the new session.
		Assert.NotNull(seen.First(s => s.Items == 1).Id);
		Assert.Equal(seen.First(s => s.Items == 1).Id, _chat.Current.Id);
		process.Write(Init, Message, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);

		Assert.NotNull(_chat.Current.Id);
		Assert.False(_chat.Current.IsDraft);
		Assert.Equal(_chat.Current.Id, Assert.Single(_chat.Sessions).Id);
	}

	[Fact]
	public async Task Reconnect_HistoryCatchUp_NoDuplicates_InterruptedLiveTextDiscarded()
	{
		var process = await StartAsync("hi");
		process.Write(Init, Delta);
		await UntilAsync(() => _chat.Current.Items.Count == 2);

		// The connection is down: the message, a tool call and the result are stored but never reach the client.
		_server.DropPushes = true;
		process.Write(Message, ToolUse, ToolResult, Message2, Result);
		process.Exit();
		await UntilAsync(() => _server.Chats.Read(_chat.Current.Id!, 0, int.MaxValue).Events.Any(e => e.Kind == ChatEventKinds.Result));
		Assert.Equal(2, _chat.Current.Items.Count);
		_server.Transport.Sent.Clear();

		_server.Reconnect();
		await UntilAsync(() => !_chat.Running);

		Assert.Equal(["hi", "Hello there.", "ls", "All done."], _chat.Current.Items.Select(i => i.Text));
		Assert.DoesNotContain(_chat.Current.Items, i => i.Streaming);
		var types = _server.Transport.Sent.Select(e => e.Type).ToList();
		Assert.Equal([MessageTypes.ChatOpen, MessageTypes.ChatHistory], types.Take(2));
		Assert.Equal(0.0123m, _chat.LastCost);
	}

	[Fact]
	public async Task StartedReply_AdoptsTheSession_WhenThePromptPushWasLost()
	{
		await ReadyAsync();
		_server.DropPushes = true;
		await _chat.SendAsync("hi");
		var process = await _server.Agent.NextAsync();

		Assert.False(_chat.Current.IsDraft);
		process.Write(Init, Message, Result);
		process.Exit();
		await UntilAsync(() => _server.Chats.Read(_chat.Current.Id!, 0, int.MaxValue).Events.Any(e => e.Kind == ChatEventKinds.Result));
		_server.Reconnect();
		await UntilAsync(() => _chat.Current.Items.Count == 2);

		Assert.Equal(["hi", "Hello there."], _chat.Current.Items.Select(i => i.Text));
		await UntilAsync(() => !_chat.Running);
	}

	[Fact]
	public async Task Reconnect_StaleLiveText_NotGluedToLaterDeltas()
	{
		var process = await StartAsync("hi");
		process.Write(Init, Delta);
		await UntilAsync(() => _chat.Current.Items is [_, { Text: "Hel" }]);

		// Hel is on screen; a second Hel is lost with the connection, a third arrives after it came back mid-message.
		_server.DropPushes = true;
		process.Write(Delta);
		await Task.Delay(100, Ct);
		_server.Reconnect();
		process.Write(Delta);
		await Task.Delay(100, Ct);

		Assert.Equal(["hi"], _chat.Current.Items.Select(i => i.Text));

		process.Write(Message, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);
		Assert.Equal(["hi", "Hello there."], _chat.Current.Items.Select(i => i.Text));
		Assert.DoesNotContain(_chat.Current.Items, i => i.Streaming);
	}

	[Fact]
	public async Task ReloadedPage_RunningSessionIsLoaded_SoStopWorks()
	{
		var process = await StartAsync("long job");
		process.Write(Init);
		await UntilAsync(() => _chat.Current.Items.Count == 1);

		var reloaded = new ChatEngine(_server.Transport, _sync, _time);
		await reloaded.InitializeAsync();
		await UntilAsync(() => reloaded.Running && reloaded.Sessions is [{ Running: true }]);
		await reloaded.StopAsync();

		await UntilAsync(() => !_chat.Running && !reloaded.Running);
		Assert.True(process.Killed);
	}

	[Fact]
	public async Task NoFolder_ErrorLine()
	{
		await _chat.InitializeAsync();
		await _chat.SendAsync("hi");

		Assert.Equal("Open a folder first.", Assert.Single(_chat.Current.Items).Text);
	}

	[Fact]
	public async Task NotConnected_SendFailureIsAnErrorLine_NotAnException()
	{
		await ReadyAsync();
		_server.Transport.SetState(AiChromeProxy.Client.Transport.TransportState.Disconnected);
		_server.Transport.Reply = _ => throw new InvalidOperationException("down");

		await _chat.SendAsync("hi");
		await _chat.StopAsync();

		Assert.Equal(SyncEngine.ConnectionLost, Assert.Single(_chat.Current.Items).Text);
	}

	[Fact]
	public async Task StoredEvent_ArrivingTwice_CountsOnce_AndOutOfOrderResultStillEndsItsRun()
	{
		var process = await StartAsync("hi");
		process.Write(Init, Message);
		await UntilAsync(() => _chat.Current.Items.Count == 2);
		var sessionId = _chat.Current.Id!;
		var runId = _chat.Current.ActiveRunId!;

		// Replays of stored events (a catch-up racing the live pushes) change nothing.
		_server.Transport.Push(Envelope.Create(MessageTypes.ChatEvent, new ChatEvent(sessionId, runId, 2, ChatEventKinds.Message, Text: "Hello there.")));
		Assert.Equal(["hi", "Hello there."], _chat.Current.Items.Select(i => i.Text));

		// The next run's prompt (seq 4) overtakes the previous run's result (seq 3).
		_server.Transport.Push(Envelope.Create(MessageTypes.ChatEvent, new ChatEvent(sessionId, "run-2", 4, ChatEventKinds.Prompt, Text: "again")));
		_server.Transport.Push(Envelope.Create(MessageTypes.ChatEvent, new ChatEvent(sessionId, runId, 3, ChatEventKinds.Result, Ok: true)));

		Assert.Equal("run-2", _chat.Current.ActiveRunId);
		Assert.True(_chat.Current.Running);
		Assert.Equal(["hi", "Hello there.", "again"], _chat.Current.Items.Select(i => i.Text));
		await ExitAsync(process);
	}

	[Fact]
	public async Task LongMessage_SeveralEvents_OneBlock()
	{
		var process = await StartAsync("q");
		var sessionId = _chat.Current.Id!;
		var runId = _chat.Current.ActiveRunId!;
		_server.Transport.Push(Envelope.Create(MessageTypes.ChatEvent, new ChatEvent(sessionId, runId, 2, ChatEventKinds.Message, Text: "part one, ")));
		_server.Transport.Push(Envelope.Create(MessageTypes.ChatEvent, new ChatEvent(sessionId, runId, 3, ChatEventKinds.Message, Text: "part two")));

		Assert.Equal(["q", "part one, part two"], _chat.Current.Items.Select(i => i.Text));
		await ExitAsync(process);
	}

	[Fact]
	public async Task Busy_SurfacedAsAnErrorLine_NotAnException()
	{
		var process = await StartAsync("one");
		await _chat.SendAsync("two");

		var error = Assert.Single(_chat.Current.Items, i => i.Kind == ChatItemKind.Error);
		Assert.Contains("already working", error.Text);
		Assert.Equal(["one", error.Text], _chat.Current.Items.Select(i => i.Text));

		process.Write(Init, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);
	}

	[Fact]
	public async Task TooLong_SurfacedAsAnErrorLine_InTheDraftOfANewChat()
	{
		await ReadyAsync();
		await _chat.SendAsync(new string('x', ChatLimits.MaxTextChars + 1));

		Assert.True(_chat.Current.IsDraft);
		Assert.Equal(ChatItemKind.Error, Assert.Single(_chat.Current.Items).Kind);
		Assert.Empty(_chat.Sessions);
	}

	[Fact]
	public async Task Approve_RoundTrip_CardOpensAndCloses()
	{
		var process = await StartAsync("list");
		process.Write(Init, ToolUse);
		await UntilAsync(() => _chat.Current.Items.Count == 2);
		var runId = _chat.Current.ActiveRunId!;

		var tool = _server.Broker.RequestAsync(runId, "Bash", new JsonObject { ["command"] = "ls" }, Ct);
		await UntilAsync(() => _chat.Current.Approvals.Count == 1);
		var card = _chat.Current.Approvals[0];
		Assert.Equal((runId, "Bash", "ls"), (card.RunId, card.Tool, card.Summary));

		await _chat.ApproveAsync(card.RequestId, ChatDecisions.Allow);

		Assert.True(await tool);
		await UntilAsync(() => _chat.Current.Approvals.Count == 0);
		var sent = _server.Transport.Sent.Last(e => e.Type == MessageTypes.ChatApprove);
		Assert.Equal(new ChatApprovePayload(runId, card.RequestId, ChatDecisions.Allow), sent.Payload.Deserialize<ChatApprovePayload>(JsonSerializerOptions.Web));
		process.Write(ToolResult, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);
	}

	[Fact]
	public async Task PendingApproval_RunStopped_CardClosesAndRunShowsCancelled()
	{
		var process = await StartAsync("list");
		process.Write(Init, ToolUse);
		await UntilAsync(() => _chat.Current.Items.Count == 2);
		_ = _server.Broker.RequestAsync(_chat.Current.ActiveRunId!, "Bash", new JsonObject { ["command"] = "ls" }, Ct);
		await UntilAsync(() => _chat.Current.Approvals.Count == 1);

		await _chat.StopAsync();
		await UntilAsync(() => !_chat.Running);

		Assert.Empty(_chat.Current.Approvals);
		Assert.Contains(_chat.Current.Items, i => i is { Kind: ChatItemKind.Error, Text: "Cancelled." });
	}

	[Fact]
	public async Task Approve_UnknownRequest_NothingSent()
	{
		var process = await StartAsync("list");
		await _chat.ApproveAsync("nope", ChatDecisions.Allow);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.ChatApprove);

		process.Exit();
		await UntilAsync(() => !_chat.Running);
	}

	[Fact]
	public async Task Stop_CancelsTheRun()
	{
		var process = await StartAsync("long job");
		process.Write(Init);

		await _chat.StopAsync();
		await UntilAsync(() => !_chat.Running);

		Assert.True(process.Killed);
		Assert.Equal("Cancelled.", _chat.Current.Items[^1].Text);
	}

	[Fact]
	public async Task NewChat_VersusContinue_ResumeOnlyInTheSameSession()
	{
		var first = await StartAsync("one");
		first.Write(Init, Message, Result);
		first.Exit();
		await UntilAsync(() => !_chat.Running);
		var sessionA = _chat.Current.Id;

		await _chat.SendAsync("two");
		var second = await _server.Agent.NextAsync();
		Assert.Equal("sess-1", second.Run.ResumeId);
		second.Write(Init, Message2, Result);
		second.Exit();
		await UntilAsync(() => !_chat.Running);
		Assert.Equal(sessionA, _chat.Current.Id);
		Assert.Equal(["one", "Hello there.", "two", "All done."], _chat.Current.Items.Select(i => i.Text));

		_chat.NewChat();
		Assert.True(_chat.Current.IsDraft);
		Assert.Empty(_chat.Current.Items);
		await _chat.SendAsync("three");
		var third = await _server.Agent.NextAsync();
		Assert.Null(third.Run.ResumeId);
		third.Write(Init, Result);
		third.Exit();
		await UntilAsync(() => !_chat.Running);

		Assert.NotEqual(sessionA, _chat.Current.Id);
		Assert.Equal(2, _chat.Sessions.Count);
		Assert.Equal(["three"], _chat.Current.Items.Select(i => i.Text));
	}

	[Fact]
	public async Task SessionsList_RunningFollowsTheRun()
	{
		var process = await StartAsync("hi");
		await UntilAsync(() => _chat.Sessions is [{ Running: true }]);

		process.Write(Init, Result);
		process.Exit();
		await UntilAsync(() => _chat.Sessions is [{ Running: false }]);
		Assert.False(_chat.Running);
	}

	[Fact]
	public async Task ReloadedPage_OpensAnOldSessionFromHistory()
	{
		var process = await StartAsync("hi");
		process.Write(Init, Message, ToolUse, ToolResult, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);
		var sessionId = _chat.Current.Id!;

		var reloaded = new ChatEngine(_server.Transport, _sync, _time);
		await reloaded.InitializeAsync();
		Assert.Equal(sessionId, Assert.Single(reloaded.Sessions).Id);
		Assert.True(reloaded.Current.IsDraft);
		await reloaded.OpenSessionAsync(sessionId);

		Assert.Equal(sessionId, reloaded.Current.Id);
		Assert.Equal(["hi", "Hello there.", "ls"], reloaded.Current.Items.Select(i => i.Text));
		Assert.Equal(0.0123m, reloaded.LastCost);
		Assert.Equal("hi", reloaded.Current.Title);
	}

	[Fact]
	public async Task Elapsed_CountsWhileRunning()
	{
		var process = await StartAsync("hi");
		Assert.Equal(TimeSpan.Zero, _chat.Elapsed);
		_time.Advance(TimeSpan.FromSeconds(42));
		Assert.Equal(TimeSpan.FromSeconds(42), _chat.Elapsed);

		process.Write(Init, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);
		Assert.Null(_chat.Elapsed);
	}

	[Fact]
	public async Task Changed_FiresForVisibleChanges()
	{
		var changes = 0;
		_chat.Changed += () => Interlocked.Increment(ref changes);
		var process = await StartAsync("hi");
		var before = changes;
		process.Write(Init, Message);
		await UntilAsync(() => _chat.Current.Items.Count == 2);

		Assert.True(changes > before);
		await ExitAsync(process);
	}

	private static async Task UntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the chat engine.");
			await Task.Delay(10, Ct);
		}
	}

	/// <summary>Ends the process and waits until the server stored the run's result (an event the test pushed by hand may not end the run itself).</summary>
	private async Task ExitAsync(FakeAgentProcess process)
	{
		process.Exit();
		await UntilAsync(() => _server.Chats.Read(_chat.Current.Id!, 0, int.MaxValue).Events.Any(e => e.Kind == ChatEventKinds.Result));
	}

	private async Task SyncedAsync()
	{
		await _sync.InitializeAsync();
		_folder.Write("a.txt", "v1");
		await _sync.OpenFolderAsync();
		await _sync.SyncOnceAsync(Ct);
	}

	private async Task ReadyAsync()
	{
		await SyncedAsync();
		await _chat.InitializeAsync();
	}

	/// <summary>Sends <paramref name="text"/> as a new chat and returns the started agent process.</summary>
	private async Task<FakeAgentProcess> StartAsync(string text)
	{
		await ReadyAsync();
		await _chat.SendAsync(text);
		return await _server.Agent.NextAsync();
	}
}
