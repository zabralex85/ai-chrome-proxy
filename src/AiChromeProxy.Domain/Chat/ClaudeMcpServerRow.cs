namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// One MCP server in <see cref="ClaudeToolsPayload"/>: <paramref name="Status"/> is one of <see cref="ClaudeToolStatuses"/>; <paramref name="OnHere"/> is
/// the <b>On in this project</b> switch (for a <c>pending</c> project server: approved); <paramref name="Plugin"/> is the plugin name of a
/// <c>plugin:&lt;plugin&gt;:&lt;server&gt;</c> server (its plugin's switch covers it); <paramref name="Reason"/> is why it failed, when known.
/// A server of the repo's <c>.mcp.json</c> carries what its entry runs (<paramref name="Command"/>: the command and its arguments, or the url; shortened)
/// and the entry's hash (<paramref name="EntryHash"/>), which an approval is pinned to (<see cref="ClaudeToolEntries.Approval"/>).
/// </summary>
public sealed record ClaudeMcpServerRow(
	string Name,
	string? Source,
	string Status,
	bool OnHere,
	string? Plugin = null,
	string? Reason = null,
	string? Command = null,
	string? EntryHash = null);
