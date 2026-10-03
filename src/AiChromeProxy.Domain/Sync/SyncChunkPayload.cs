namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// <c>sync.chunk</c>: up to <see cref="SyncLimits.ChunkSize"/> raw bytes of a needed file, in order;
/// <c>data</c> is base64url without padding (<see cref="SyncData.Encode"/> / <see cref="SyncData.Decode"/>), never plain base64.
/// </summary>
public sealed record SyncChunkPayload(string Repo, string Path, long Offset, string Data, bool Last, string? Sha256 = null);
