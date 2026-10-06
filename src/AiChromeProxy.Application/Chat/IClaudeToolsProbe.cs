using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>Asks Claude Code for its MCP servers and plugins without a model call (<b>Check now</b>).</summary>
public interface IClaudeToolsProbe
{
	/// <summary>Runs the checks in <paramref name="folder"/> (the repo's mirror).</summary>
	/// <returns>A snapshot (<see cref="ClaudeToolsSnapshot.FromCheck"/>, <see cref="ClaudeToolsSnapshot.CheckedAt"/> not set), or null and why the check failed.</returns>
	Task<(ClaudeToolsSnapshot? Snapshot, string? Error)> CheckAsync(string folder);
}
