namespace AiChromeProxy.Client.Chat;

/// <summary>A file the browser reported for the chat box.</summary>
/// <param name="Handle">The browser's key for it (<see cref="IAttachmentSource"/>).</param>
/// <param name="Name">Its name (pasted pictures are named by the time).</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="Type">Its media type, or empty.</param>
/// <param name="Preview">An object URL of the picture for <c>image/*</c>, else null.</param>
public sealed record AttachmentFile(string Handle, string Name, long Size, string Type, string? Preview);
