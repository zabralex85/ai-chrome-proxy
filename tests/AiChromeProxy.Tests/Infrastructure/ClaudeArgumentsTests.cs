using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Infrastructure.Chat;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class ClaudeArgumentsTests
{
	private static readonly string[] Base = ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages"];

	[Fact]
	public void Convention_MentionsBothCodeLinkForms()
	{
		Assert.Contains("`path:line`", ClaudeArguments.Convention);
		Assert.Contains("`path#Symbol`", ClaudeArguments.Convention);
	}

	[Fact]
	public void Ask_WithApprovalEndpoint_AddsPromptToolAndMcpConfig()
	{
		var args = ClaudeArguments.Build(Run("ask", "http://127.0.0.1:5180/mcp/approve", "tok123"), new AgentOptions());

		Assert.Equal(Base, args.Take(5));
		Assert.Equal(["--permission-mode", "acceptEdits", "--permission-prompts", "host", "--permission-prompt-tool", "mcp__aicp__approve", "--mcp-config"], args.Skip(5).Take(7));
		using (var config = JsonDocument.Parse(args[12]))
		{
			var server = config.RootElement.GetProperty("mcpServers").GetProperty("aicp");
			Assert.Equal("http", server.GetProperty("type").GetString());
			Assert.Equal("http://127.0.0.1:5180/mcp/approve", server.GetProperty("url").GetString());
			Assert.Equal("Bearer tok123", server.GetProperty("headers").GetProperty("Authorization").GetString());
		}

		Assert.Equal(["--append-system-prompt", ClaudeArguments.Convention], args.Skip(13));
	}

	[Fact]
	public void AgentRun_ToString_HidesTheToken()
	{
		var text = Run("ask", "http://127.0.0.1:5180/mcp/approve", "tok123").ToString();

		Assert.DoesNotContain("tok123", text, StringComparison.Ordinal);
		Assert.Contains("ApprovalToken = ***", text, StringComparison.Ordinal);
		Assert.Contains("ApprovalUrl = http://127.0.0.1:5180/mcp/approve", text, StringComparison.Ordinal);
		Assert.Contains("ApprovalToken =  }", new AgentRun("C:/r", "hi").ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("ask")]
	[InlineData("unknown")]
	public void Ask_WithoutApprovalEndpoint_OnlyAcceptsEdits(string permissions)
	{
		var args = ClaudeArguments.Build(Run(permissions), new AgentOptions());

		Assert.Equal([.. Base, "--permission-mode", "acceptEdits", "--append-system-prompt", ClaudeArguments.Convention], args);
	}

	[Theory]
	[InlineData("all", "bypassPermissions")]
	[InlineData("settings", "dontAsk")]
	public void AllAndSettings_SetModeAndIgnoreApproval(string permissions, string mode)
	{
		var args = ClaudeArguments.Build(Run(permissions, "http://127.0.0.1:1/mcp/approve", "t"), new AgentOptions());

		Assert.Equal([.. Base, "--permission-mode", mode, "--append-system-prompt", ClaudeArguments.Convention], args);
	}

	[Fact]
	public void ResumeModelAndTools_OnlyWhenSet_AllowedToolsEndedByNextFlag()
	{
		var args = ClaudeArguments.Build(Run("settings", resume: "abc", model: "opus", tools: ["Bash(git status)", "WebFetch"]), new AgentOptions { Args = ["--x", "1"] });

		Assert.Equal(
			[.. Base, "--resume", "abc", "--permission-mode", "dontAsk", "--model", "opus", "--allowedTools", "Bash(git status)", "WebFetch", "--append-system-prompt", ClaudeArguments.Convention, "--x", "1"],
			args);
		Assert.DoesNotContain("--resume", ClaudeArguments.Build(Run(), new AgentOptions()));
		Assert.DoesNotContain("--model", ClaudeArguments.Build(Run(model: " "), new AgentOptions()));
		Assert.DoesNotContain("--allowedTools", ClaudeArguments.Build(Run(tools: []), new AgentOptions()));
	}

	[Fact]
	public void AllowedTools_SkipsEmptyAndFlagLikeRules()
	{
		var args = ClaudeArguments.Build(Run("settings", tools: ["", " ", "--evil", "Read"]), new AgentOptions());

		Assert.Equal([.. Base, "--permission-mode", "dontAsk", "--allowedTools", "Read", "--append-system-prompt", ClaudeArguments.Convention], args);
		Assert.DoesNotContain("--allowedTools", ClaudeArguments.Build(Run("settings", tools: ["", "-x"]), new AgentOptions()));
	}

	[Fact]
	public void ResolveCommand_PrefersExeInAnyDirOverCmd()
	{
		string[] dirs = [@"C:\a", "", @"C:\b"];

		Assert.Equal(@"C:\b\claude.exe", ClaudeArguments.ResolveCommand("claude", dirs, f => f is @"C:\b\claude.exe" or @"C:\a\claude.cmd"));
		Assert.Equal(@"C:\a\claude.cmd", ClaudeArguments.ResolveCommand("claude", dirs, f => f is @"C:\a\claude.cmd"));
		Assert.Null(ClaudeArguments.ResolveCommand("claude", dirs, _ => false));
		Assert.Equal(@"C:\b\tool.cmd", ClaudeArguments.ResolveCommand("tool.cmd", dirs, f => f == @"C:\b\tool.cmd"));
	}

	[Theory]
	[InlineData(@"C:\tools\claude.exe")]
	[InlineData(@"bin\claude")]
	public void ResolveCommand_RootedOrQualified_KeptWhenItExists_NullOtherwise(string command)
	{
		Assert.Equal(command, ClaudeArguments.ResolveCommand(command, [], f => f == command));
		Assert.Null(ClaudeArguments.ResolveCommand(command, [], _ => false));
	}

	[Fact]
	public void UnsafeForCmd_FlagsMetacharactersOnly()
	{
		Assert.False(ClaudeArguments.UnsafeForCmd(["-p", "{\"a\":\"b\"}", "Bash(git status)", "path with space"]));
		Assert.True(ClaudeArguments.UnsafeForCmd(["Bash(a && b)"]));
		Assert.True(ClaudeArguments.UnsafeForCmd(["50%"]));
		Assert.True(ClaudeArguments.UnsafeForCmd(["a\nb"]));
	}

	[Fact]
	public void CmdLine_QuotesEveryItemAndWrapsOnce()
	{
		var line = ClaudeArguments.CmdLine(@"C:\Program Files\npm\claude.cmd", ["-p", "say \"hi\"", @"dir\"]);

		Assert.Equal("\"\"C:\\Program Files\\npm\\claude.cmd\" \"-p\" \"say \\\"hi\\\"\" \"dir\\\\\"\"", line);
	}

	private static AgentRun Run(string permissions = "ask", string? url = null, string? token = null, string? resume = null, string? model = null, string[]? tools = null)
		=> new(@"C:\mirror\repo", "hello", resume, permissions, model, tools, url, token);
}
