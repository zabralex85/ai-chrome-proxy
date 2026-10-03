namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.stored</c>: the reply to the last chunk once the file is in the mirror with the expected hash.</summary>
public sealed record SyncStoredPayload(string Repo, string Path);
