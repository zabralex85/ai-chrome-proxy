namespace AiChromeProxy.Domain.Projects;

/// <summary>A file of the mirror: protocol path, size in bytes and last write time (UTC).</summary>
public sealed record MirrorFile(string Path, long Size, DateTimeOffset Modified);
