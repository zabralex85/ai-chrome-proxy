using System.Text;
using System.Text.Json;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Domain;

public sealed class ChatContractTests
{
	public static TheoryData<ChatEvent, string> EventShapes => new()
	{
		{ new ChatEvent("s", "u", 1, ChatEventKinds.Prompt, Text: "hi"), """{"sessionId":"s","runId":"u","seq":1,"kind":"prompt","text":"hi","toolId":null,"name":null,"summary":null,"isError":null,"requestId":null,"decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 1, ChatEventKinds.Text, Text: "he"), """{"sessionId":"s","runId":"u","seq":1,"kind":"text","text":"he","toolId":null,"name":null,"summary":null,"isError":null,"requestId":null,"decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 2, ChatEventKinds.Message, Text: "hello"), """{"sessionId":"s","runId":"u","seq":2,"kind":"message","text":"hello","toolId":null,"name":null,"summary":null,"isError":null,"requestId":null,"decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 3, ChatEventKinds.Tool, ToolId: "t1", Name: "Bash", Summary: "ls"), """{"sessionId":"s","runId":"u","seq":3,"kind":"tool","text":null,"toolId":"t1","name":"Bash","summary":"ls","isError":null,"requestId":null,"decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 4, ChatEventKinds.ToolResult, ToolId: "t1", IsError: false, Summary: "out"), """{"sessionId":"s","runId":"u","seq":4,"kind":"toolResult","text":null,"toolId":"t1","name":null,"summary":"out","isError":false,"requestId":null,"decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 5, ChatEventKinds.Permission, RequestId: "q", Name: "Bash", Summary: "rm x"), """{"sessionId":"s","runId":"u","seq":5,"kind":"permission","text":null,"toolId":null,"name":"Bash","summary":"rm x","isError":null,"requestId":"q","decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 6, ChatEventKinds.PermissionResolved, RequestId: "q", Decision: ChatDecisions.Deny), """{"sessionId":"s","runId":"u","seq":6,"kind":"permissionResolved","text":null,"toolId":null,"name":null,"summary":null,"isError":null,"requestId":"q","decision":"deny","ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 7, ChatEventKinds.Result, Ok: true, CostUsd: 0.12m, DurationMs: 4200), """{"sessionId":"s","runId":"u","seq":7,"kind":"result","text":null,"toolId":null,"name":null,"summary":null,"isError":null,"requestId":null,"decision":null,"ok":true,"costUsd":0.12,"durationMs":4200,"error":null,"truncated":null}""" },
		{ new ChatEvent("s", "u", 8, ChatEventKinds.Permission, RequestId: "q", Name: "Bash", Summary: "rm x", Truncated: true), """{"sessionId":"s","runId":"u","seq":8,"kind":"permission","text":null,"toolId":null,"name":"Bash","summary":"rm x","isError":null,"requestId":"q","decision":null,"ok":null,"costUsd":null,"durationMs":null,"error":null,"truncated":true}""" },
	};

	[Fact]
	public void Payloads_AreCamelCase()
	{
		Assert.Equal("""{"repo":"r"}""", Json(new ChatOpenPayload("r")));
		Assert.Equal("""{"repo":"r","sessions":[{"id":"s","title":"t","updated":"2026-10-04T10:00:00+00:00","running":true}]}""", Json(new ChatSessionsPayload("r", [new ChatSessionInfo("s", "t", new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), true)])));
		Assert.Equal("""{"sessionId":"s","afterSeq":3}""", Json(new ChatHistoryPayload("s", 3)));
		Assert.Equal("""{"repo":"r","sessionId":null,"text":"hi"}""", Json(new ChatSendPayload("r", null, "hi")));
		Assert.Equal("""{"sessionId":"s","runId":"u"}""", Json(new ChatStartedPayload("s", "u")));
		Assert.Equal("""{"runId":"u"}""", Json(new ChatCancelPayload("u")));
		Assert.Equal("""{"runId":"u","requestId":"q","decision":"allowAlways"}""", Json(new ChatApprovePayload("u", "q", ChatDecisions.AllowAlways)));
	}

	[Fact]
	public void Send_WithoutSessionId_Deserializes()
	{
		var payload = JsonSerializer.Deserialize<ChatSendPayload>("""{"repo":"r","text":"hi"}""", JsonSerializerOptions.Web)!;

		Assert.Null(payload.SessionId);
		Assert.Equal("hi", payload.Text);
	}

	[Theory]
	[MemberData(nameof(EventShapes))]
	public void Event_ExactJson_RoundTrips(ChatEvent value, string expected)
	{
		Assert.Equal(expected, Json(value));
		Assert.Equal(value, JsonSerializer.Deserialize<ChatEvent>(expected, JsonSerializerOptions.Web));
	}

	[Fact]
	public void Constants_HaveWireValues()
	{
		Assert.Equal(16_000, ChatLimits.MaxTextChars);
		Assert.Equal(24_000, ChatLimits.MaxEventBytes);
		Assert.Equal(2_048, ChatLimits.ToolSummaryBytes);
		Assert.Equal(20_000, ChatLimits.PermissionSummaryBytes);
		Assert.Equal(200, ChatLimits.MaxNameBytes);
		Assert.Equal(30_000, ChatLimits.MaxSendBytes);
		Assert.Equal("busy", ErrorCodes.Busy);
		Assert.Equal(
			["chat.open", "chat.sessions", "chat.history", "chat.events", "chat.send", "chat.started", "chat.cancel", "chat.approve", "chat.event"],
			[MessageTypes.ChatOpen, MessageTypes.ChatSessions, MessageTypes.ChatHistory, MessageTypes.ChatEvents, MessageTypes.ChatSend, MessageTypes.ChatStarted, MessageTypes.ChatCancel, MessageTypes.ChatApprove, MessageTypes.ChatEvent]);
	}

	[Fact]
	public void Split_ShortEvent_IsUnchanged()
	{
		var e = new ChatEvent("s", "u", 1, ChatEventKinds.Message, Text: "hello");

		Assert.Same(e, Assert.Single(ChatEventSplitter.Split(e)));
	}

	[Theory]
	[InlineData("a")]
	[InlineData("\U0001F600")]
	[InlineData("\"")]
	[InlineData("é")]
	public void Split_LongMessage_PagesFitAndConcatenate(string unit)
	{
		var text = string.Concat(Enumerable.Repeat(unit, 100_000 / unit.Length));
		var e = new ChatEvent("s", "u", 9, ChatEventKinds.Message, Text: text);

		var parts = ChatEventSplitter.Split(e);

		Assert.True(parts.Count > 1);
		Assert.All(parts, p => Assert.True(JsonSerializer.SerializeToUtf8Bytes(p, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes));
		Assert.All(parts, p => Assert.True(p.Text!.Length <= ChatLimits.MaxTextChars));
		Assert.All(parts, p => Assert.Equal((e.SessionId, e.RunId, e.Seq, e.Kind), (p.SessionId, p.RunId, p.Seq, p.Kind)));
		Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
	}

	[Theory]
	[InlineData("\"")]
	[InlineData("<")]
	public void Split_LongestPromptOfEscapedCharacters_PartsFit(string unit)
	{
		var text = string.Concat(Enumerable.Repeat(unit, ChatLimits.MaxTextChars));

		var parts = ChatEventSplitter.Split(new ChatEvent("s", "u", 1, ChatEventKinds.Prompt, Text: text));

		Assert.True(parts.Count > 1);
		Assert.All(parts, p => Assert.Equal(ChatEventKinds.Prompt, p.Kind));
		Assert.All(parts, p => Assert.True(JsonSerializer.SerializeToUtf8Bytes(p, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes));
		Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
	}

	[Fact]
	public void Split_SmallerLimit_PartsFitIt()
	{
		var text = new string('a', 30_000);

		var parts = ChatEventSplitter.Split(new ChatEvent("s", "u", 1, ChatEventKinds.Message, Text: text), 10_000);

		Assert.True(parts.Count >= 3);
		Assert.All(parts, p => Assert.True(JsonSerializer.SerializeToUtf8Bytes(p, JsonSerializerOptions.Web).Length <= 10_000));
		Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
	}

	[Fact]
	public void Truncate_ByUtf8Bytes_WithEllipsis_ShortUnchanged()
	{
		var cut = ChatEventSplitter.Truncate(new string('é', 100), 21);

		Assert.Equal(new string('é', 9) + "…", cut);
		Assert.Equal("short", ChatEventSplitter.Truncate("short"));
		Assert.Equal(ChatLimits.ToolSummaryBytes, Encoding.UTF8.GetByteCount(ChatEventSplitter.Truncate(new string('a', 5_000))));
	}

	[Fact]
	public void Split_NeverCutsSurrogatePair()
	{
		// An odd prefix shifts every pair across the 16 000-character boundary.
		var text = "x" + string.Concat(Enumerable.Repeat("\U0001F600", 40_000));

		var parts = ChatEventSplitter.Split(new ChatEvent("s", "u", 1, ChatEventKinds.Text, Text: text));

		Assert.All(parts, p => Assert.False(char.IsHighSurrogate(p.Text![^1]) || char.IsLowSurrogate(p.Text[0])));
		Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
	}

	[Fact]
	public void Split_Summary_TruncatedWithEllipsis_ByUtf8Bytes()
	{
		var tool = new ChatEvent("s", "u", 1, ChatEventKinds.ToolResult, ToolId: "t", Summary: new string('a', 5_000));
		var emoji = tool with { Summary = string.Concat(Enumerable.Repeat("\U0001F600", 1_000)) };

		var a = Assert.Single(ChatEventSplitter.Split(tool)).Summary!;
		var b = Assert.Single(ChatEventSplitter.Split(emoji)).Summary!;

		Assert.Equal(ChatLimits.ToolSummaryBytes, Encoding.UTF8.GetByteCount(a));
		Assert.EndsWith("…", a, StringComparison.Ordinal);
		Assert.True(Encoding.UTF8.GetByteCount(b) <= ChatLimits.ToolSummaryBytes);
		Assert.EndsWith("…", b, StringComparison.Ordinal);
		Assert.Equal("short", Assert.Single(ChatEventSplitter.Split(tool with { Summary = "short" })).Summary);
	}

	[Fact]
	public void Split_PermissionSummary_KeptWhole_BeyondTheToolSummaryLimit()
	{
		var command = new string('a', 10_000);
		var e = new ChatEvent("s", "u", 1, ChatEventKinds.Permission, Name: "Bash", Summary: command, RequestId: "q");

		var part = Assert.Single(ChatEventSplitter.Split(e));

		Assert.Equal(command, part.Summary);
		Assert.Null(part.Truncated);
	}

	[Theory]
	[InlineData("a")]
	[InlineData("\"")]
	[InlineData("ж")]
	[InlineData("\U0001F600")]
	public void Split_PermissionSummaryTooLong_CutToItsBudget_MarkedTruncated(string unit)
	{
		var summary = string.Concat(Enumerable.Repeat(unit, 40_000 / unit.Length));
		var e = new ChatEvent("s", "u", 1, ChatEventKinds.Permission, Name: "Bash", Summary: summary, RequestId: "q");

		var part = Assert.Single(ChatEventSplitter.Split(e));

		Assert.True(part.Truncated);
		Assert.EndsWith("…", part.Summary!, StringComparison.Ordinal);
		Assert.StartsWith(part.Summary![..^1], summary, StringComparison.Ordinal);
		Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(part.Summary, JsonSerializerOptions.Web).Length, ChatLimits.PermissionSummaryBytes - 20, ChatLimits.PermissionSummaryBytes);
		Assert.True(JsonSerializer.SerializeToUtf8Bytes(part, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes - 1_024);
	}

	[Fact]
	public void FitJson_FittingStringIsTheSameInstance()
	{
		var s = new string('"', 3_000);

		Assert.Same(s, ChatEventSplitter.FitJson(s, 18_002));
		Assert.NotSame(s, ChatEventSplitter.FitJson(s, 18_001));
	}

	[Theory]
	[InlineData(ChatEventKinds.Tool)]
	[InlineData(ChatEventKinds.Permission)]
	public void Split_Name_CappedLikeASummary(string kind)
	{
		var e = new ChatEvent("s", "u", 1, kind, Name: new string('n', 5_000), Summary: "x");

		var name = Assert.Single(ChatEventSplitter.Split(e)).Name!;

		Assert.Equal(ChatLimits.MaxNameBytes, Encoding.UTF8.GetByteCount(name));
		Assert.EndsWith("…", name, StringComparison.Ordinal);
	}

	[Fact]
	public void ProjectSettings_AgentKeys_RoundTrip_UnknownKeysKept()
	{
		var json = """{"agentPermissions":"all","agentModel":"opus","agentAllowedTools":["Bash(ls)","Read"],"other":1}""";

		var settings = JsonSerializer.Deserialize<ProjectSettings>(json, JsonSerializerOptions.Web)!;

		Assert.Equal("all", settings.AgentPermissions);
		Assert.Equal("opus", settings.AgentModel);
		Assert.Equal(["Bash(ls)", "Read"], settings.AgentAllowedTools);
		Assert.Contains("\"other\":1", JsonSerializer.Serialize(settings, JsonSerializerOptions.Web), StringComparison.Ordinal);
		Assert.DoesNotContain("OrDefault", JsonSerializer.Serialize(settings, JsonSerializerOptions.Web), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null, "ask")]
	[InlineData("", "ask")]
	[InlineData("bogus", "ask")]
	[InlineData("ask", "ask")]
	[InlineData("all", "all")]
	[InlineData("settings", "settings")]
	public void AgentPermissionsOrDefault_UnknownFallsBackToAsk(string? value, string expected)
	{
		Assert.Equal(expected, new ProjectSettings { AgentPermissions = value }.AgentPermissionsOrDefault);
	}

	private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
