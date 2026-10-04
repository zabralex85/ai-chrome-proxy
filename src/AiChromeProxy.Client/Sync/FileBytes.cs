namespace AiChromeProxy.Client.Sync;

/// <summary>A file read for the viewer: at most <see cref="Navigator.FileText.MaxBytes"/> + 1 of its bytes, and its real size.</summary>
/// <param name="Bytes">The leading bytes (never more than <c>FileText.MaxBytes + 1</c>).</param>
/// <param name="Size">The file's size in bytes.</param>
public sealed record FileBytes(byte[] Bytes, long Size);
