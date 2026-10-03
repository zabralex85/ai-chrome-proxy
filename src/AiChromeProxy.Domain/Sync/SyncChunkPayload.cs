namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.chunk</c>: up to <see cref="SyncLimits.ChunkSize"/> raw bytes (base64 in <c>data</c>) of a needed file, in order.</summary>
public sealed record SyncChunkPayload(string Repo, string Path, long Offset, string Data, bool Last, string? Sha256 = null);
