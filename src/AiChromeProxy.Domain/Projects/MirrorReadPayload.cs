namespace AiChromeProxy.Domain.Projects;

/// <summary><c>mirror.read {repo, path, offset}</c>: client → server, one chunk of a mirror file from <paramref name="Offset"/>; reply <c>mirror.data</c> (a <c>Sync.SyncDataPayload</c>, or <c>not_found</c>).</summary>
public sealed record MirrorReadPayload(string Repo, string Path, long Offset);
