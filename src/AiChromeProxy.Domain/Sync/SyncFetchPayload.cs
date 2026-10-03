namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.fetch {repo, path, offset}</c>: client → server, asks for a mirror file from <c>Offset</c>; reply <c>sync.data</c> (or <c>not_found</c>).</summary>
public sealed record SyncFetchPayload(string Repo, string Path, long Offset);
