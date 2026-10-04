namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.sessions</c>: the repo's sessions, newest first.</summary>
public sealed record ChatSessionsPayload(string Repo, IReadOnlyList<ChatSessionInfo> Sessions);
