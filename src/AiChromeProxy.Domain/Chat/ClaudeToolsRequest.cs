namespace AiChromeProxy.Domain.Chat;

/// <summary><c>agent.tools.get</c> and <c>agent.tools.check</c>: the repo whose Claude tools to show or check.</summary>
public sealed record ClaudeToolsRequest(string Repo);
