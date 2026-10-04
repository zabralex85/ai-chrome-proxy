namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.send</c>: a user message (at most <see cref="ChatLimits.MaxTextChars"/> characters); no <paramref name="SessionId"/> starts a new session.</summary>
public sealed record ChatSendPayload(string Repo, string? SessionId, string Text);
