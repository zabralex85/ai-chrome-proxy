namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.cancel</c>: stops a run; echoed back.</summary>
public sealed record ChatCancelPayload(string RunId);
