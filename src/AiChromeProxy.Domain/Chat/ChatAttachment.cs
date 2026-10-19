namespace AiChromeProxy.Domain.Chat;

/// <summary>A file attached to a message, as the history lists it (the content stays on the server).</summary>
public sealed record ChatAttachment(string Name, long Size);
