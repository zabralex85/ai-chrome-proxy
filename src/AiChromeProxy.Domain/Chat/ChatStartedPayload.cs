namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.started</c>: the reply to <c>chat.send</c> once the run is accepted.</summary>
public sealed record ChatStartedPayload(string SessionId, string RunId);
