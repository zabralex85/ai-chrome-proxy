namespace AiChromeProxy.Application.Chat;

/// <summary>A server of the repo's <c>.mcp.json</c>: what it runs (shortened, for display) and the hash of its entry (<see cref="McpJson.Hash"/>).</summary>
public sealed record McpJsonEntry(string? Command, string Hash);
