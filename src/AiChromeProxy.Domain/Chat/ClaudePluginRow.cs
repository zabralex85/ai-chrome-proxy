namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// One plugin in <see cref="ClaudeToolsPayload"/>: <paramref name="Id"/> is <c>name@marketplace</c>; <paramref name="Enabled"/> is Claude Code's own
/// setting; <paramref name="OnHere"/> is the <b>On in this project</b> switch; <paramref name="Status"/> is <c>off</c>, <c>missing</c> or <c>unknown</c>
/// (see <see cref="ClaudeToolStatuses"/>) when it says more than <paramref name="Enabled"/>, else null.
/// </summary>
public sealed record ClaudePluginRow(string Id, string Name, string? Version, bool Enabled, bool OnHere, string? Status = null);
