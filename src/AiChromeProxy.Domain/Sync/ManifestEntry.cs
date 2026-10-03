namespace AiChromeProxy.Domain.Sync;

/// <summary>One synced file: <c>{path, size, sha256}</c> (SHA-256 as 64 lower-case hex characters).</summary>
public sealed record ManifestEntry(string Path, long Size, string Sha256);
