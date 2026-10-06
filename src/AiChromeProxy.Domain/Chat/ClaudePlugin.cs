namespace AiChromeProxy.Domain.Chat;

/// <summary>An installed plugin as Claude Code reports it: <paramref name="Id"/> is <c>name@marketplace</c>.</summary>
public sealed record ClaudePlugin(string Id, string Name, string? Version, bool Enabled);
