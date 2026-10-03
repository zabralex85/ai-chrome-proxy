namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.manifest</c>: one page of the full manifest; the last page has <c>final: true</c>.</summary>
public sealed record SyncManifestPayload(string Repo, IReadOnlyList<ManifestEntry> Entries, bool Final);
