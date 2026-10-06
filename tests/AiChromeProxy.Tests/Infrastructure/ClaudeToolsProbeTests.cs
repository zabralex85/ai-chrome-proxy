using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Infrastructure.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

/// <summary><see cref="ClaudeToolsProbe"/> against the fake agent (<c>FAKE_AGENT_MCP_LIST</c>, <c>FAKE_AGENT_PLUGIN_LIST</c>).</summary>
public sealed class ClaudeToolsProbeTests : IDisposable
{
	private static readonly string FakeAgent = Path.Combine(AppContext.BaseDirectory, "AiChromeProxy.FakeAgent.exe");
	private static readonly string McpList = Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "claude-tools", "mcp-list-2.1.289.txt");

	private readonly string _folder = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _plugins;

	public ClaudeToolsProbeTests()
	{
		Directory.CreateDirectory(_folder);
		_plugins = Path.Combine(_folder, "plugins.json");
		File.WriteAllText(_plugins, """[{"id":"design@market","version":"1.0.0","scope":"user","enabled":false},{"id":"security@market","enabled":true}]""");
	}

	public void Dispose() => Directory.Delete(_folder, recursive: true);

	[Fact]
	public async Task Check_ParsesBothCommands_StderrNoiseSkipped_RunsInTheFolder()
	{
		// A relative answer file is found only when the command runs in the folder.
		var (snapshot, error) = await Probe(env: new() { ["FAKE_AGENT_PLUGIN_LIST"] = "plugins.json" }).CheckAsync(_folder);

		Assert.Null(error);
		Assert.NotNull(snapshot);
		Assert.Equal(ClaudeToolsSnapshot.FromCheck, snapshot.From);
		Assert.Equal(10, snapshot.Servers.Count);
		Assert.DoesNotContain(snapshot.Servers, s => s.Name.Contains("mcp-sdk", StringComparison.Ordinal));
		Assert.Equal(new ClaudeMcpServer("codegraph", null, ClaudeToolStatuses.Connected), snapshot.Servers.Single(s => s.Name == "codegraph"));
		Assert.Equal(ClaudeToolStatuses.Pending, snapshot.Servers.Single(s => s.Name == "team-db").Status);
		Assert.Equal([new ClaudePlugin("design@market", "design", "1.0.0", false), new ClaudePlugin("security@market", "security", null, true)], snapshot.Plugins);
	}

	[Fact]
	public async Task Check_Timeout_KillsAndSaysSo()
	{
		var probe = Probe(env: new() { ["FAKE_AGENT_PROBE_DELAY_MS"] = "30000" }, timeout: TimeSpan.FromSeconds(1));

		var (snapshot, error) = await probe.CheckAsync(_folder).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

		Assert.Null(snapshot);
		Assert.Equal("Check timed out after 1 s.", error);
	}

	[Fact]
	public async Task Check_NonZeroExit_ErrorWithTheLastStderrLine()
	{
		var (snapshot, error) = await Probe(env: new() { ["FAKE_AGENT_EXIT_CODE"] = "2" }).CheckAsync(_folder);

		Assert.Null(snapshot);
		Assert.Equal("Check failed: `AiChromeProxy.FakeAgent mcp list` exited with code 2: [mcp-sdk] fake: a warning on stderr", error);
	}

	[Fact]
	public async Task Check_PluginListNotJson_Error()
	{
		await File.WriteAllTextAsync(_plugins, "Usage: claude plugin list", TestContext.Current.CancellationToken);

		var (snapshot, error) = await Probe().CheckAsync(_folder);

		Assert.Null(snapshot);
		Assert.Equal("Check failed: `claude plugin list --json` did not print JSON.", error);
	}

	[Fact]
	public async Task Check_CommandMissing_Error()
	{
		var (snapshot, error) = await Probe(command: Path.Combine(_folder, "missing", "claude.exe")).CheckAsync(_folder);

		Assert.Null(snapshot);
		Assert.StartsWith("Check failed: ", error, StringComparison.Ordinal);
		Assert.Contains("Agent:Command", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Timeout_Is60Seconds()
	{
		Assert.Equal(TimeSpan.FromSeconds(60), new ClaudeToolsProbe(Options.Create(new AgentOptions()), NullLogger<ClaudeToolsProbe>.Instance).Timeout);
	}

	private ClaudeToolsProbe Probe(string? command = null, Dictionary<string, string>? env = null, TimeSpan? timeout = null)
	{
		var options = new AgentOptions
		{
			Command = command ?? FakeAgent,
			Env = { ["FAKE_AGENT_MCP_LIST"] = McpList, ["FAKE_AGENT_PLUGIN_LIST"] = _plugins },
		};
		foreach (var (key, value) in env ?? [])
		{
			options.Env[key] = value;
		}

		return new ClaudeToolsProbe(Options.Create(options), NullLogger<ClaudeToolsProbe>.Instance) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
	}
}
