namespace AiChromeProxy.Client.Sync;

/// <summary>A file found by the walk; <see cref="Modified"/> is the browser's <c>lastModified</c> (ms since the epoch).</summary>
public sealed record FileMeta(string Path, long Size, long Modified);
