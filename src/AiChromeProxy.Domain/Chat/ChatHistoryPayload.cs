namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.history</c>: asks for stored events with <c>seq</c> greater than <paramref name="AfterSeq"/>; reply <c>chat.events</c>.</summary>
public sealed record ChatHistoryPayload(string SessionId, long AfterSeq);
