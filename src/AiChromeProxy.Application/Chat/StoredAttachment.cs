namespace AiChromeProxy.Application.Chat;

/// <summary>A finished attachment on the server.</summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Name">Its file name.</param>
/// <param name="Size">Its length in bytes.</param>
public sealed record StoredAttachment(string Path, string Name, long Size);
