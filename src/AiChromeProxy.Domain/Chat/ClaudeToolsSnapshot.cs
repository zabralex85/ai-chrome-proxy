namespace AiChromeProxy.Domain.Chat;

/// <summary>What Claude Code last reported about its MCP servers and plugins for a repo: from a run's <c>init</c> event or from <b>Check now</b>.</summary>
/// <param name="Servers">The MCP servers.</param>
/// <param name="Plugins">The plugins (a run lists only the loaded ones).</param>
/// <param name="CheckedAt">When it was reported (UTC).</param>
/// <param name="From"><see cref="FromRun"/> or <see cref="FromCheck"/>.</param>
public sealed record ClaudeToolsSnapshot(IReadOnlyList<ClaudeMcpServer> Servers, IReadOnlyList<ClaudePlugin> Plugins, DateTimeOffset CheckedAt, string From)
{
	/// <summary>Reported by a run's <c>init</c> event.</summary>
	public const string FromRun = "run";

	/// <summary>Reported by <c>claude mcp list</c> and <c>claude plugin list --json</c>.</summary>
	public const string FromCheck = "check";
}
