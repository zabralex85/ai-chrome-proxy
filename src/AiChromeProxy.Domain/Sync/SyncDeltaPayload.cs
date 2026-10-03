namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.delta</c>: changes since the last manifest or delta; answered with <c>sync.need</c> for the upserts.</summary>
public sealed record SyncDeltaPayload(string Repo, IReadOnlyList<ManifestEntry> Upserts, IReadOnlyList<string> Deletes);
