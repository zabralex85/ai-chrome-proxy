namespace AiChromeProxy.Domain.Chat;

/// <summary>One chat session in <see cref="ChatSessionsPayload"/>; <paramref name="Updated"/> is a UTC timestamp.</summary>
public sealed record ChatSessionInfo(string Id, string Title, DateTimeOffset Updated, bool Running);
