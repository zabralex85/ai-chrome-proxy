namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.remote {repo, changes}</c>: server → client, changes made in the mirror (at most <see cref="SyncLimits.MaxPageEntries"/> per page).</summary>
public sealed record SyncRemotePayload(string Repo, IReadOnlyList<RemoteChange> Changes);
