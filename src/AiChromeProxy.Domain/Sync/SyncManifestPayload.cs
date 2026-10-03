namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// <c>sync.manifest</c>: one page of the full manifest; the last page has <c>final: true</c>. <c>keep</c> (optional) lists what the browser
/// could not sync now but must not be deleted on the mirror: file paths, and folder prefixes ending in <c>/</c> (nothing is uploaded for them).
/// </summary>
public sealed record SyncManifestPayload(string Repo, IReadOnlyList<ManifestEntry> Entries, bool Final, IReadOnlyList<string>? Keep = null);
