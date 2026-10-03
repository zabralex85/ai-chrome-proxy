namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.data</c>: server → client, a part of a mirror file (<c>Data</c> is base64url); the last part also carries the whole file's <c>Sha256</c>.</summary>
public sealed record SyncDataPayload(string Repo, string Path, long Offset, string Data, bool Last, string? Sha256 = null);
