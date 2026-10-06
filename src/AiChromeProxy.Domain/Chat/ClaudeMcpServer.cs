namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// An MCP server as Claude Code reports it: <paramref name="Source"/> is <c>user</c>, <c>project</c>, <c>local</c>, <c>plugin</c>, <c>claudeai</c> or
/// null when unknown; <paramref name="Status"/> as reported (<see cref="ClaudeToolStatuses"/>); <paramref name="Reason"/> is why it failed.
/// </summary>
public sealed record ClaudeMcpServer(string Name, string? Source, string Status, string? Reason = null);
