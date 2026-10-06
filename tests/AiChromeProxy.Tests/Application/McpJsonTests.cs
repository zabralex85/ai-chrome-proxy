using System.Text;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Infrastructure.Sync;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Application;

/// <summary>The repo's <c>.mcp.json</c> and the approvals pinned to its entries.</summary>
public sealed class McpJsonTests : IDisposable
{
	private const string Repo = "repo";
	private const string TeamDb = """{"mcpServers":{"team-db":{"command":"npx","args":["-y","db-mcp"],"env":{"B":"2","A":"1"}},"docs":{"type":"http","url":"https://mcp.example.com/docs"}}}""";

	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FileSystemMirrorStore _mirror;

	public McpJsonTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, Repo));
		_mirror = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	[Fact]
	public void Hash_KeyOrderAndWhitespaceIndependent_ValuesAndArrayOrderCount()
	{
		var hash = McpJson.Hash(JsonNode.Parse("""{"command":"npx","args":["-y","db"],"env":{"A":"1","B":{"x":1,"y":2}}}"""));

		Assert.Matches("^[0-9a-f]{64}$", hash);
		Assert.Equal(hash, McpJson.Hash(JsonNode.Parse("""{ "env": { "B": { "y": 2, "x": 1 }, "A": "1" }, "args": [ "-y", "db" ], "command": "npx" }""")));
		Assert.NotEqual(hash, McpJson.Hash(JsonNode.Parse("""{"command":"npx","args":["db","-y"],"env":{"A":"1","B":{"x":1,"y":2}}}""")));
		Assert.NotEqual(hash, McpJson.Hash(JsonNode.Parse("""{"command":"cmd","args":["-y","db"],"env":{"A":"1","B":{"x":1,"y":2}}}""")));
		Assert.Equal(McpJson.Hash(null), McpJson.Hash(JsonNode.Parse("null")));
	}

	[Fact]
	public void Parse_CommandWithArgs_ElseUrl_Shortened()
	{
		var longArg = new string('x', 400);
		var entries = McpJson.Parse($$$"""{"mcpServers":{"a":{"command":"npx","args":["-y","db-mcp",3]},"b":{"url":"https://x/mcp"},"c":{"type":"stdio"},"d":{"command":"run","args":["{{{longArg}}}"]},"e":7}}""");

		Assert.Equal(["a", "b", "c", "d", "e"], entries.Keys.Order(StringComparer.Ordinal));
		Assert.Equal("npx -y db-mcp 3", entries["a"].Command);
		Assert.Equal("https://x/mcp", entries["b"].Command);
		Assert.Null(entries["c"].Command);
		Assert.Equal(McpJson.MaxCommandLength, entries["d"].Command!.Length);
		Assert.EndsWith("x…", entries["d"].Command, StringComparison.Ordinal);
		Assert.Null(entries["e"].Command);
		Assert.Equal(McpJson.Hash(JsonNode.Parse("""{"url":"https://x/mcp"}""")), entries["b"].Hash);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("""{"mcpServers":[]}""")]
	[InlineData("""{"other":{}}""")]
	[InlineData("""{"mcpServers":{"a":{},"a":{}}}""")]
	public void Parse_InvalidOrWithoutServers_NoEntries(string json) => Assert.Empty(McpJson.Parse(json));

	[Fact]
	public void Approved_OnlyUnchangedEntries_ChangedGoneUnhashedAndAicpNot()
	{
		var entries = McpJson.Parse(TeamDb);
		var teamDb = ClaudeToolEntries.Approval("team-db", entries["team-db"].Hash);
		var changed = McpJson.Parse(TeamDb.Replace("\"npx\"", "\"cmd\"", StringComparison.Ordinal));

		Assert.Equal("team-db |", Lists(McpJson.Approved([teamDb, " "], entries)));
		Assert.Equal("| team-db", Lists(McpJson.Approved([teamDb], changed)));
		Assert.Equal("| team-db", Lists(McpJson.Approved([teamDb], McpJson.Parse("{}"))));
		Assert.Equal("| docs", Lists(McpJson.Approved(["docs"], entries)));
		Assert.Equal("|", Lists(McpJson.Approved([ClaudeToolEntries.Approval("aicp", entries["team-db"].Hash), "aicp"], McpJson.Parse("""{"mcpServers":{"aicp":{"command":"npx","args":["-y","db-mcp"],"env":{"B":"2","A":"1"}}}}"""))));
		Assert.Equal("|", Lists(McpJson.Approved(null, entries)));
	}

	[Fact]
	public async Task ReadAsync_TheRepoRootFile()
	{
		await File.WriteAllTextAsync(Path.Combine(_root, Repo, McpJson.FileName), TeamDb, Ct);

		var entries = await McpJson.ReadAsync(_mirror, Repo, Ct);

		Assert.Equal(McpJson.Parse(TeamDb)["team-db"], entries["team-db"]);
		Assert.Equal("npx -y db-mcp", entries["team-db"].Command);
	}

	[Fact]
	public async Task ReadAsync_MissingInvalidTooBigOrNoRepo_NoEntries()
	{
		var file = Path.Combine(_root, Repo, McpJson.FileName);
		Assert.Empty(await McpJson.ReadAsync(_mirror, Repo, Ct));
		Assert.Empty(await McpJson.ReadAsync(_mirror, "..", Ct));

		await File.WriteAllTextAsync(file, "{ not json", Ct);
		Assert.Empty(await McpJson.ReadAsync(_mirror, Repo, Ct));

		await File.WriteAllTextAsync(file, TeamDb.Replace("\"docs\"", "\"pad\":\"" + new string(' ', McpJson.MaxBytes) + "\",\"docs\"", StringComparison.Ordinal), Encoding.UTF8, Ct);
		Assert.Empty(await McpJson.ReadAsync(_mirror, Repo, Ct));
	}

	/// <summary>"approved | stale", each list space-separated.</summary>
	private static string Lists((IReadOnlyList<string> Approved, IReadOnlyList<string> Stale) result) =>
		$"{string.Join(' ', result.Approved)} | {string.Join(' ', result.Stale)}".Trim();
}
