namespace AiChromeProxy.Domain.Chat;

/// <summary>Rules for the per-project lists of MCP server names and plugin ids (<c>agentDisabledMcpServers</c> and the others).</summary>
public static class ClaudeToolEntries
{
	/// <summary>Longest name or id, in characters.</summary>
	public const int MaxLength = 200;

	/// <summary>Most entries per list.</summary>
	public const int MaxCount = 100;

	/// <summary>The app's own approval MCP server: never denied and not shown.</summary>
	public const string ApprovalServer = "aicp";

	/// <summary>The usable entries: trimmed, non-empty, without control characters, at most <see cref="MaxLength"/> characters, distinct, the first <see cref="MaxCount"/>.</summary>
	public static IReadOnlyList<string> Clean(IEnumerable<string?>? entries) =>
		[.. (entries ?? []).Select(e => e?.Trim() ?? string.Empty)
			.Where(e => e.Length is > 0 and <= MaxLength && !e.Any(char.IsControl))
			.Distinct(StringComparer.Ordinal)
			.Take(MaxCount)];
}
