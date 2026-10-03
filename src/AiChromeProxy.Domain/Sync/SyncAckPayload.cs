namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.ack {repo, path, sha256?}</c>: client → server, the change is applied (null hash = deleted); the server echoes it.</summary>
public sealed record SyncAckPayload(string Repo, string Path, string? Sha256);
