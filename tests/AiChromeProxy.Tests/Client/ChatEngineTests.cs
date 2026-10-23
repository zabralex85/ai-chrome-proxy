using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Transport;
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
	public async Task StreamedAnswer_TextAfterAToolCall_StreamsToo()
	{
		var process = await StartAsync("hi");
		process.Write(Init, Delta, Message, ToolUse, ToolResult);
		await UntilAsync(() => _chat.Current.Items.Count == 3);

		process.Write(Delta);

		await UntilAsync(() => _chat.Current.Items is [_, _, _, { Streaming: true }]);
		Assert.Equal("Hel", _chat.Current.Items[3].Text);
		process.Write(Message2, Result);
		process.Exit();
		await UntilAsync(() => !_chat.Running);
		Assert.Equal(["hi", "Hello there.", "ls", "All done."], _chat.Current.Items.Select(i => i.Text));
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
		Assert.Equal(new ChatItem(ChatItemKind.Error, string.Empty, "The message is too long; shorten it.", IsError: true), Assert.Single(_chat.Current.Items));
		Assert.Empty(_chat.Sessions);
	}

	[Theory]
	[InlineData('\u0436')]
	[InlineData('"')]
	public async Task TooBigForTheHubOnceEscaped_RefusedWithoutSending(char c)
	{
		await ReadyAsync();

		await _chat.SendAsync(new string(c, ChatLimits.MaxTextChars));

		Assert.True(_chat.Current.IsDraft);
		Assert.Equal("The message is too long; shorten it.", Assert.Single(_chat.Current.Items).Text);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.ChatSend);
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
	public async Task Approve_LongCommandShownWhole_TooLongToShowOnlyDenied()
	{
		var process = await StartAsync("list");
		process.Write(Init, ToolUse);
		await UntilAsync(() => _chat.Current.Items.Count == 2);
		var runId = _chat.Current.ActiveRunId!;
		var command = "echo " + new string('a', 10_000);

		var first = _server.Broker.RequestAsync(runId, "Bash", new JsonObject { ["command"] = command }, Ct);
		await UntilAsync(() => _chat.Current.Approvals.Count == 1);
		Assert.Equal((command, false), (_chat.Current.Approvals[0].Summary, _chat.Current.Approvals[0].Truncated));
		Assert.True(await _chat.ApproveAsync(_chat.Current.Approvals[0].RequestId, ChatDecisions.Allow));
		Assert.True(await first);
		await UntilAsync(() => _chat.Current.Approvals.Count == 0);

		var second = _server.Broker.RequestAsync(runId, "Bash", new JsonObject { ["command"] = "echo " + new string('a', 30_000) }, Ct);
		await UntilAsync(() => _chat.Current.Approvals.Count == 1);
		var card = _chat.Current.Approvals[0];
		Assert.True(card.Truncated);

		Assert.False(await _chat.ApproveAsync(card.RequestId, ChatDecisions.Allow));
		Assert.Contains(_chat.Current.Items, i => i.Text == PermissionBroker.TooLongToShow);
		Assert.Single(_chat.Current.Approvals);
		Assert.True(await _chat.ApproveAsync(card.RequestId, ChatDecisions.Deny));
		Assert.False(await second);
		await UntilAsync(() => _chat.Current.Approvals.Count == 0);
		await ExitAsync(process);
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
	public async Task Stop_ShowsStoppingAtOnce_UntilTheRunEnded_AndPressingTwiceSendsOneCancel()
	{
		var process = await StartAsync("long job");
		process.Write(Init);
		var changes = 0;
		_chat.Changed += () => changes++;
		Assert.False(_chat.Stopping);

		var release = new TaskCompletionSource();
		var reply = _server.Transport.Reply!;
		_server.Transport.Reply = async e =>
		{
			if (e.Type == MessageTypes.ChatCancel)
			{
				await release.Task;
			}

			return await reply(e);
		};
		var first = _chat.StopAsync();
		Assert.True(_chat.Stopping);
		Assert.True(changes > 0);
		await _chat.StopAsync();
		release.SetResult();
		await first;
		await UntilAsync(() => !_chat.Running);

		Assert.False(_chat.Stopping);
		Assert.Single(_server.Transport.Sent, e => e.Type == MessageTypes.ChatCancel);
	}

	[Fact]
	public async Task Stop_WhenTheRequestFails_StoppingIsOffAgain()
	{
		var process = await StartAsync("long job");
		process.Write(Init);
		var reply = _server.Transport.Reply!;
		_server.Transport.Reply = e => e.Type == MessageTypes.ChatCancel ? throw new InvalidOperationException("down") : reply(e);

		await _chat.StopAsync();

		Assert.False(_chat.Stopping);
		Assert.True(_chat.Running);
	}

	[Fact]
	public async Task Stop_InOneFolder_DoesNotLeaveTheNextFoldersRunStopping()
	{
		var process = await StartAsync("long job");
		process.Write(Init);
		var release = new TaskCompletionSource();
		var reply = _server.Transport.Reply!;
		_server.Transport.Reply = async e =>
		{
			if (e.Type == MessageTypes.ChatCancel)
			{
				await release.Task;
			}

			return await reply(e);
		};
		var stopping = _chat.StopAsync();
		Assert.True(_chat.Stopping);
		var repoA = _chat.Repo;

		// Another page already runs a job in the other folder, so opening it lists a running session.
		var otherFolder = new FakeFolder { Name = "Other Repo", PickResult = "Other Repo" };
		otherFolder.Write("b.txt", "v1");
		var otherSync = new SyncEngine(_server.Transport, otherFolder, TimeProvider.System);
		await otherSync.InitializeAsync();
		await otherSync.OpenFolderAsync();
		await otherSync.SyncOnceAsync(Ct);
		var otherChat = new ChatEngine(_server.Transport, otherSync, _time);
		await otherChat.InitializeAsync();
		await otherChat.SendAsync("other job");
		await _server.Agent.NextAsync();

		_folder.Name = "Other Repo";
		_folder.PickResult = "Other Repo";
		_folder.Files.Clear();
		_folder.Write("b.txt", "v1");
		await _sync.OpenFolderAsync();
		await _sync.SyncOnceAsync(Ct);
		await UntilAsync(() => _chat.Repo != repoA && _chat.Running);

		Assert.False(_chat.Stopping);
		release.SetResult();
		await stopping;
	}

	[Fact]
	public async Task Stop_WithoutAKnownRunId_StopsTheFoldersRun()
	{
		var process = await StartAsync("long job");
		process.Write(Init);
		await UntilAsync(() => _chat.Current.Items.Count == 1);

		// The reloaded page sees the run in the listing, but the history that tells its run id is still on its way.
		var history = new TaskCompletionSource();
		var reply = _server.Transport.Reply!;
		_server.Transport.Reply = async e =>
		{
			if (e.Type == MessageTypes.ChatHistory)
			{
				await history.Task;
			}

			return await reply(e);
		};
		var reloaded = new ChatEngine(_server.Transport, _sync, _time);
		var initializing = reloaded.InitializeAsync();
		await UntilAsync(() => reloaded.Running);
		Assert.Null(reloaded.Current.ActiveRunId);
		Assert.All(reloaded.Sessions, s => Assert.True(s.Running));

		await reloaded.StopAsync();
		await UntilAsync(() => process.Killed);
		history.SetResult();
		await initializing;

		var cancel = _server.Transport.Sent.Last(e => e.Type == MessageTypes.ChatCancel).Payload.Deserialize<ChatCancelPayload>(JsonSerializerOptions.Web)!;
		Assert.Equal((null, _sync.Repo), (cancel.RunId, cancel.Repo));
		await UntilAsync(() => !_chat.Running && !reloaded.Running);
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

	[Fact]
	public async Task Send_WithAttachments_StartsTheRun_AndTheUserItemListsThem()
	{
		await ReadyAsync();
		var repo = _chat.Repo!;
		_server.Attachments.Append(new ChatAttachPayload(repo, "0123456789abcdef0123456789abcdef", "shot.png", 1, 0, "AQ", true));

		Assert.True(await _chat.SendAsync("look", ["0123456789abcdef0123456789abcdef"]));
		var process = await _server.Agent.NextAsync();

		Assert.Contains("shot.png", process.Run.Prompt);
		await UntilAsync(() => _chat.Current.Items.Any(i => i.Kind == ChatItemKind.User));
		var user = _chat.Current.Items.First(i => i.Kind == ChatItemKind.User);
		Assert.Equal("look", user.Text);
		Assert.Equal([new ChatAttachment("shot.png", 1)], user.Attachments);
	}

	[Fact]
	public async Task Send_OnlyAttachments_TitlesTheSessionWithTheFirstName()
	{
		await ReadyAsync();
		var repo = _chat.Repo!;
		_server.Attachments.Append(new ChatAttachPayload(repo, "0123456789abcdef0123456789abcdef", "shot.png", 1, 0, "AQ", true));

		Assert.True(await _chat.SendAsync(string.Empty, ["0123456789abcdef0123456789abcdef"]));

		await UntilAsync(() => _chat.Current.Title == "shot.png");
	}

	[Fact]
	public async Task Send_AnExpiredAttachment_ReturnsFalse_WithTheServersMessage()
	{
		await ReadyAsync();

		Assert.False(await _chat.SendAsync("look", ["ffffffffffffffffffffffffffffffff"]));

		Assert.Contains(_chat.Current.Items, i => i.Kind == ChatItemKind.Error && i.Text == "An attachment has expired; attach it again.");
	}

	[Fact]
	public async Task View_OtherReposChatReadOnly_BackReopensTheFolders()
	{
		var other = _server.Chats.CreateSession("Other_PC", "their question");
		_server.Chats.Append(other.Id, [new ChatEvent(other.Id, "r1", 0, ChatEventKinds.Prompt, Text: "their question")]);
		await ReadyAsync();
		await UntilAsync(() => _chat.Repo == "My_Repo");

		await _chat.ViewAsync("Other_PC");
		await UntilAsync(() => _chat.Repo == "Other_PC" && _chat.Sessions.Count == 1);

		Assert.Equal("Other_PC", _chat.Viewing);
		Assert.Equal("their question", _chat.Sessions[0].Title);
		Assert.False(await _chat.SendAsync("hi"));
		Assert.Contains(_chat.Current.Items, i => i.Kind == ChatItemKind.Error && i.Text == ChatEngine.ReadOnlyNote);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.ChatSend);

		await _chat.ViewAsync(null);
		await UntilAsync(() => _chat.Repo == "My_Repo");
		Assert.Null(_chat.Viewing);
		Assert.Empty(_chat.Sessions);
	}

	[Fact]
	public async Task View_BackBeforeTheFolderOpens_StillReadOnly_AndToolsRefused()
	{
		_server.Chats.CreateSession("Other_PC", "their question");
		await ReadyAsync();
		await UntilAsync(() => _chat.Repo == "My_Repo");
		await _chat.ViewAsync("Other_PC");
		await UntilAsync(() => _chat.Repo == "Other_PC");

		// Back to the folder, but its chat.open is still on its way: the subscription is on the other project.
		_server.Transport.ReplyDelay = TimeSpan.FromMinutes(5);
		var back = _chat.ViewAsync(null);
		Assert.Null(_chat.Viewing);

		Assert.False(await _chat.SendAsync("hi"));
		await _chat.StopAsync();
		Assert.False(await _chat.ApproveAsync("nope", ChatDecisions.Allow));
		await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.ToolsAsync(check: true));
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.ChatSend or MessageTypes.ChatCancel or MessageTypes.ChatApprove or MessageTypes.AgentToolsGet or MessageTypes.AgentToolsCheck);

		_server.Transport.SetState(AiChromeProxy.Client.Transport.TransportState.Reconnecting);
		await back;
	}

	[Fact]
	public async Task View_RunningElsewhere_StopAndApproveSendNothing()
	{
		await ReadyAsync();
		_server.Chat.Send(new ChatSendPayload("My_Repo", null, "work"), new EnvelopeContext("other-tab", null, (_, _) => Task.CompletedTask));
		var process = await _server.Agent.NextAsync();

		// The same repo viewed from "another computer": the run is listed as running, but this page may not stop it.
		await _chat.ViewAsync("My_Repo");
		await UntilAsync(() => _chat.Running);
		await _chat.StopAsync();
		Assert.False(await _chat.ApproveAsync("nope", ChatDecisions.Allow));

		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.ChatCancel or MessageTypes.ChatApprove);
		process.Exit();
	}

	[Fact]
	public async Task View_BackWhileItsOpenIsOnItsWay_EndsOnTheFoldersChat_AndWritesNothingMeanwhile()
	{
		_server.Chats.CreateSession("Other_PC", "their question");
		await ReadyAsync();
		await UntilAsync(() => _chat.Repo == "My_Repo");

		// Pick the other project, then Back before its chat.open answered: the server's subscription already moved there.
		_server.Transport.ReplyDelay = TimeSpan.FromMilliseconds(300);
		var view = _chat.ViewAsync("Other_PC");
		var back = _chat.ViewAsync(null);

		Assert.True(_chat.IsReadOnly);
		await WritesNothingAsync();

		await Task.WhenAll(view, back).WaitAsync(TimeSpan.FromSeconds(10), Ct);
		await UntilAsync(() => _chat.Repo == "My_Repo" && !_chat.IsReadOnly);
		Assert.Null(_chat.Viewing);
		Assert.Empty(_chat.Sessions);
		Assert.Equal("My_Repo", _server.Transport.Sent.Last(e => e.Type == MessageTypes.ChatOpen).Payload.Deserialize<ChatOpenPayload>(JsonSerializerOptions.Web)!.Repo);
	}

	[Fact]
	public async Task View_BackWithoutAFolderWhileItsOpenIsOnItsWay_ShowsNothing_AndItsPushesStayOut()
	{
		await _chat.InitializeAsync();
		_server.Transport.ReplyDelay = TimeSpan.FromMilliseconds(300);
		var view = _chat.ViewAsync("Other_PC");
		await _chat.ViewAsync(null);
		await view.WaitAsync(TimeSpan.FromSeconds(10), Ct);

		Assert.Null(_chat.Repo);
		Assert.Null(_chat.Viewing);
		Assert.True(_chat.IsReadOnly);

		// The subscription is still on the other project: its runs reach this page, but stay out of it.
		_server.Transport.ReplyDelay = TimeSpan.Zero;
		var pushed = 0;
		_server.Transport.Received += e => Interlocked.Add(ref pushed, e.Type == MessageTypes.ChatEvent ? 1 : 0); // runs after the engine's handler
		Directory.CreateDirectory(_server.PathOf("Other_PC", string.Empty));
		_server.Chat.Send(new ChatSendPayload("Other_PC", null, "work"), new EnvelopeContext("other-tab", null, (_, _) => Task.CompletedTask));
		var process = await _server.Agent.NextAsync();
		process.Write(Init, Delta);
		await UntilAsync(() => Volatile.Read(ref pushed) >= 2);
		Assert.False(_chat.Running);
		Assert.Empty(_chat.Current.Items);
		await WritesNothingAsync();
		process.Exit();
	}

	[Fact]
	public async Task View_PickAgainWhileTheFoldersOpenIsOnItsWay_EndsOnTheViewedChat()
	{
		_server.Chats.CreateSession("Other_PC", "their question");
		await ReadyAsync();
		await _chat.ViewAsync("Other_PC");
		await UntilAsync(() => _chat.Repo == "Other_PC");

		_server.Transport.ReplyDelay = TimeSpan.FromMilliseconds(300);
		var back = _chat.ViewAsync(null);
		var again = _chat.ViewAsync("Other_PC");
		await Task.WhenAll(back, again).WaitAsync(TimeSpan.FromSeconds(10), Ct);

		await UntilAsync(() => _chat.Repo == "Other_PC" && _chat.Sessions.Count == 1);
		Assert.Equal("Other_PC", _chat.Viewing);
		Assert.True(_chat.IsReadOnly);
		Assert.Equal("Other_PC", _server.Transport.Sent.Last(e => e.Type == MessageTypes.ChatOpen).Payload.Deserialize<ChatOpenPayload>(JsonSerializerOptions.Web)!.Repo);
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

	/// <summary>Send, Stop, Approve and the tools are refused while <see cref="ChatEngine.IsReadOnly"/>: none of them reaches the server.</summary>
	private async Task WritesNothingAsync()
	{
		Assert.False(await _chat.SendAsync("hi"));
		await _chat.StopAsync();
		Assert.False(await _chat.ApproveAsync("nope", ChatDecisions.Allow));
		await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.ToolsAsync(check: false));
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.ChatSend or MessageTypes.ChatCancel or MessageTypes.ChatApprove or MessageTypes.AgentToolsGet or MessageTypes.AgentToolsCheck or MessageTypes.ChatAttach);
	}
}
