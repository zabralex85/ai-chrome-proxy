namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.events</c>: one page of stored events; <paramref name="Final"/> is true on the last page.</summary>
public sealed record ChatEventsPayload(string SessionId, IReadOnlyList<ChatEvent> Events, bool Final);
