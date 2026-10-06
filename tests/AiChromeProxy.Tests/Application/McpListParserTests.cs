using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Tests.Application;

public sealed class McpListParserTests
{
	[Fact]
	public void RecordedOutput_EveryServer_NoiseSkipped()
	{
		// `claude mcp list` of Claude Code 2.1.289, names and paths sanitized.
		var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "claude-tools", "mcp-list-2.1.289.txt"));

		var servers = McpListParser.Parse(text);

		Assert.Equal(
			[
				new ClaudeMcpServer("claude.ai Docs", "claudeai", ClaudeToolStatuses.Connected),
				new ClaudeMcpServer("claude.ai Tracker", "claudeai", ClaudeToolStatuses.NeedsAuth),
				new ClaudeMcpServer("plugin:security:scanner", "plugin", ClaudeToolStatuses.Connected),
				new ClaudeMcpServer("plugin:design:chat", "plugin", ClaudeToolStatuses.NeedsAuth),
				new ClaudeMcpServer("plugin:design:google calendar", "plugin", ClaudeToolStatuses.NotConfigured),
				new ClaudeMcpServer("remote-tools", null, ClaudeToolStatuses.Failed, "ECONNREFUSED: ECONNREFUSED: Unable to connect. Is the computer able to access the url?"),
				new ClaudeMcpServer("events", null, ClaudeToolStatuses.Failed, "SSE error: ECONNREFUSED: Unable to connect. Is the computer able to access the url?"),
				new ClaudeMcpServer("codegraph", null, ClaudeToolStatuses.Connected),
				new ClaudeMcpServer("designer", null, ClaudeToolStatuses.Connected),
				new ClaudeMcpServer("team-db", null, ClaudeToolStatuses.Pending),
			],
			servers);
	}

	[Theory]
	[InlineData("a: cmd - ✔ Connected", "a", ClaudeToolStatuses.Connected)]
	[InlineData("a: cmd - ✗ Failed to connect", "a", ClaudeToolStatuses.Failed)]
	[InlineData("a: cmd - ⏸️ Pending approval", "a", ClaudeToolStatuses.Pending)]
	[InlineData("a: cmd - ? Something new", "a", ClaudeToolStatuses.Unknown)]
	[InlineData("a: cmd - 🟢 Something new", "a", ClaudeToolStatuses.Unknown)]
	[InlineData("a: python - --flag - - x - ✔ Connected\r", "a", ClaudeToolStatuses.Connected)]
	[InlineData("plugin:design:chat: https://chat.example.com/mcp (HTTP) - ! Needs authentication", "plugin:design:chat", ClaudeToolStatuses.NeedsAuth)]
	public void Line_NameAndStatus(string line, string name, string status)
	{
		var server = Assert.Single(McpListParser.Parse(line));

		Assert.Equal((name, status), (server.Name, server.Status));
		Assert.Null(server.Reason);
	}

	[Fact]
	public void Caps_AtMost500Servers_LongNamesSkipped_ReasonCut()
	{
		var lines = Enumerable.Range(0, 600).Select(i => $"s{i}: cmd - ✔ Connected").Prepend($"{new string('n', 201)}: cmd - ✔ Connected").Prepend($"bad: cmd - ✘ Failed to connect — {new string('r', 400)}");

		var servers = McpListParser.Parse(string.Join(Environment.NewLine, lines));

		Assert.Equal(ClaudeToolEntries.MaxReported, servers.Count);
		Assert.Equal(("bad", ClaudeToolEntries.MaxReasonLength), (servers[0].Name, servers[0].Reason!.Length));
		Assert.Equal(("s0", "s498"), (servers[1].Name, servers[^1].Name));
	}

	[Theory]
	[InlineData("")]
	[InlineData("Checking MCP server health…")]
	[InlineData("[mcp-sdk] warning: x - ! y")]
	[InlineData("no separator here")]
	public void NoiseLines_Skipped(string line)
	{
		Assert.Empty(McpListParser.Parse(line));
	}
}
