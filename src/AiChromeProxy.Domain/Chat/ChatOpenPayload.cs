namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.open</c>: subscribes the connection to a repo's chat; reply <c>chat.sessions</c>.</summary>
public sealed record ChatOpenPayload(string Repo);
