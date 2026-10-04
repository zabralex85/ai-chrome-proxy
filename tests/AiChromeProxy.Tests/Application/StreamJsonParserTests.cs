using System.Text;
using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Tests.Application;

public sealed class StreamJsonParserTests
{
	[Fact]
	public void TextAnswer_MessageAndResult_AndSessionId()
	{
		var (parser, events) = Run("text-answer.jsonl");

		Assert.Equal("sess-1", parser.ClaudeSessionId);
		Assert.Equal(["message:Hello there.", "result:True:0.0123:1234:"], events.Select(Describe));
		Assert.All(events, e => Assert.True(e.SessionId == string.Empty && e.RunId == string.Empty && e.Seq == 0));
	}

	[Fact]
	public void RealClaudeCodeOutput_ThinkingSkipped_DeltasThenMessage_Result()
	{
		// Captured from Claude Code 2.1.288 (`-p --output-format stream-json --verbose --include-partial-messages`); hook lines, usage
		// and the thinking text removed.
		var (parser, events) = Run("real-claude-2.1.288.jsonl");
		const string Answer = "ok\n\n```mermaid\ngraph LR\n    A --> B\n```";

		Assert.False(string.IsNullOrEmpty(parser.ClaudeSessionId));
		Assert.Equal(Answer, string.Concat(events.Where(e => e.Kind == ChatEventKinds.Text).Select(e => e.Text)));
		Assert.Equal(
			[$"message:{Answer}", "result:True:0.2464372:4544:"],
			events.Where(e => e.Kind != ChatEventKinds.Text).Select(Describe));
	}

	[Fact]
	public void ToolUse_SummariesAndResults()
	{
		var (parser, events) = Run("tool-use.jsonl");

		Assert.Equal("sess-2", parser.ClaudeSessionId);
		Assert.Equal(
			[
				"message:Building.",
				"tool:toolu_1:Bash:dotnet build",
				"toolResult:toolu_1:False:Build succeeded.",
				"tool:toolu_2:Read:src/Program.cs",
				"tool:toolu_3:Glob:**/*.cs",
				"tool:toolu_4:WebFetch:{\"url\":\"https://example.com\",\"prompt\":\"x\"}",
				"toolResult:toolu_2:False:line 1\nline 2",
				"toolResult:toolu_3:True:denied",
				"result:True:0.5:500:",
			],
			events.Select(Describe));
	}

	[Fact]
	public void ErrorResult_CarriesErrorText_OrSubtype()
	{
		var (_, events) = Run("error-result.jsonl");

		Assert.Equal(
			[
				"result:False:0:77:No conversation found with session ID: sess-0",
				"result:False::5:error_max_turns",
			],
			events.Select(Describe));
	}

	[Fact]
	public void PartialMessages_OnlyTextDeltas_ThenMessage()
	{
		var (_, events) = Run("partial-messages.jsonl");

		Assert.Equal(["text:Hel", "text:lo", "message:Hello"], events.Select(Describe));
	}

	[Fact]
	public void UnknownAndMalformed_AreSkipped_ParserContinues()
	{
		var (parser, events) = Run("unknown-and-malformed.jsonl");

		Assert.Equal("sess-5", parser.ClaudeSessionId);
		Assert.Equal(["message:ok", "message:after"], events.Select(Describe));
	}

	[Fact]
	public void LongSummaryAndError_AreCappedAtTwoKilobytes()
	{
		var parser = new StreamJsonParser();
		var big = new string('x', 10_000);

		var tool = parser.Feed($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Bash","input":{"command":"{{{big}}}"}}]}}""");
		var result = parser.Feed($$"""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"{{big}}"}""");
		var toolResult = parser.Feed($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":"{{{big}}}"}]}}""");

		Assert.True(Encoding.UTF8.GetByteCount(tool[0].Summary!) <= ChatLimits.ToolSummaryBytes);
		Assert.True(Encoding.UTF8.GetByteCount(result[0].Error!) <= ChatLimits.ToolSummaryBytes);
		Assert.True(Encoding.UTF8.GetByteCount(toolResult[0].Summary!) <= ChatLimits.ToolSummaryBytes);
		Assert.EndsWith("…", result[0].Error);
	}

	[Fact]
	public void TextToolText_MessageReplacesPrecedingDeltas_InOrder()
	{
		var (_, events) = Run("text-tool-text.jsonl");

		Assert.Equal(
			[
				"text:Let ",
				"text:me look.",
				"message:Let me look.",
				"tool:toolu_1:Bash:ls",
				"toolResult:toolu_1:False:a.txt",
				"text:Found ",
				"text:a.txt.",
				"message:Found a.txt.",
				"result:True:0.01:9:",
			],
			events.Select(Describe));
	}

	[Fact]
	public void LongMessageAndDelta_AreSplit_NotTruncated()
	{
		var parser = new StreamJsonParser();
		var big = new string('y', 40_000);

		var message = parser.Feed($$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{big}}}"}]}}""");
		var delta = parser.Feed($$$$"""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"{{{{big}}}}"}}}""");

		Assert.True(message.Count > 1);
		Assert.All(message, e => Assert.Equal(ChatEventKinds.Message, e.Kind));
		Assert.Equal(big, string.Concat(message.Select(e => e.Text)));
		Assert.Equal(big, string.Concat(delta.Select(e => e.Text)));
		Assert.All(message.Concat(delta), e => Assert.True(JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length <= ChatLimits.MaxEventBytes));
	}

	[Fact]
	public void ErrorFlagWithSuccessSubtype_AndNoText_SaysError()
	{
		var events = new StreamJsonParser().Feed("""{"type":"result","subtype":"success","is_error":true}""");

		Assert.Equal("error", events[0].Error);
		Assert.False(events[0].Ok);
	}

	[Fact]
	public void CompactJsonSummary_KeepsNonAsciiReadable()
	{
		var events = new StreamJsonParser().Feed("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a","name":"X","input":{"q":"привет & <b>"}}]}}""");

		Assert.Equal("{\"q\":\"привет & <b>\"}", events[0].Summary);
	}

	[Fact]
	public void FilePath_InsideCwd_IsRelative_OutsideStaysAbsolute()
	{
		var parser = new StreamJsonParser();
		parser.Feed("""{"type":"system","subtype":"init","session_id":"s","cwd":"C:\\mirror\\repo"}""");

		var events = parser.Feed("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a","name":"Edit","input":{"file_path":"C:\\mirror\\repo\\src\\A.cs"}},{"type":"tool_use","id":"b","name":"Write","input":{"file_path":"D:\\other\\B.cs"}}]}}""");

		Assert.Equal(["src/A.cs", "D:\\other\\B.cs"], events.Select(e => e.Summary));
	}

	private static (StreamJsonParser Parser, List<ChatEvent> Events) Run(string fixture)
	{
		var parser = new StreamJsonParser();
		var events = new List<ChatEvent>();
		foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "stream-json", fixture)))
		{
			events.AddRange(parser.Feed(line));
		}

		return (parser, events);
	}

	private static string Describe(ChatEvent e) => e.Kind switch
	{
		ChatEventKinds.Text or ChatEventKinds.Message => $"{e.Kind}:{e.Text}",
		ChatEventKinds.Tool => $"tool:{e.ToolId}:{e.Name}:{e.Summary}",
		ChatEventKinds.ToolResult => $"toolResult:{e.ToolId}:{e.IsError}:{e.Summary}",
		ChatEventKinds.Result => $"result:{e.Ok}:{e.CostUsd}:{e.DurationMs}:{e.Error}",
		_ => throw new InvalidOperationException($"Unexpected kind {e.Kind}"),
	};
}
