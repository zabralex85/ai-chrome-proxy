namespace AiChromeProxy.Domain.Chat;

/// <summary>Rules for the per-project lists of MCP server names and plugin ids (<c>agentDisabledMcpServers</c> and the others).</summary>
public static class ClaudeToolEntries
{
	/// <summary>Longest name or id, in characters.</summary>
	public const int MaxLength = 200;

	/// <summary>Longest approval (<see cref="Approval"/>: a name, <c>#</c> and a SHA-256 hex hash).</summary>
	public const int MaxApprovalLength = MaxLength + 1 + HashLength;

	/// <summary>Most entries per list.</summary>
	public const int MaxCount = 100;

	/// <summary>Most servers or plugins kept from one report of Claude Code.</summary>
	public const int MaxReported = 500;

	/// <summary>Longest reason a failed server keeps, in characters.</summary>
	public const int MaxReasonLength = 300;

	/// <summary>The app's own approval MCP server: never denied, never approved and not shown.</summary>
	public const string ApprovalServer = "aicp";

	private const int HashLength = 64;

	/// <summary>The usable entries: trimmed, non-empty, without control characters, at most <paramref name="maxLength"/> characters, distinct, the first <see cref="MaxCount"/>.</summary>
	public static IReadOnlyList<string> Clean(IEnumerable<string?>? entries, int maxLength = MaxLength) =>
		[.. (entries ?? []).Select(e => e?.Trim() ?? string.Empty)
			.Where(e => e.Length > 0 && e.Length <= maxLength && !e.Any(char.IsControl))
			.Distinct(StringComparer.Ordinal)
			.Take(MaxCount)];

	/// <summary>An entry of <c>agentApprovedMcpServers</c>: a project <c>.mcp.json</c> server pinned to the hash of its entry (<c>&lt;name&gt;#&lt;hash&gt;</c>).</summary>
	public static string Approval(string name, string hash) => name + "#" + hash;

	/// <summary>The server name of an approval (an entry without a hash, from before approvals were pinned, is all name).</summary>
	public static string ApprovalName(string approval)
	{
		var hash = approval.LastIndexOf('#');
		return hash >= 0 && approval.Length - hash - 1 == HashLength ? approval[..hash] : approval;
	}
}
