namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// <c>agent.tools</c>: the repo's MCP servers and plugins as the Project settings tab shows them; <paramref name="CheckedAt"/> (UTC) and
/// <paramref name="From"/> (<c>run</c> or <c>check</c>) are null when nothing was recorded yet; <paramref name="Error"/> tells why a check failed.
/// </summary>
public sealed record ClaudeToolsPayload(
	string Repo,
	IReadOnlyList<ClaudeMcpServerRow> Servers,
	IReadOnlyList<ClaudePluginRow> Plugins,
	DateTimeOffset? CheckedAt = null,
	string? From = null,
	string? Error = null);
