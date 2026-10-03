namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.need</c>: the paths of a manifest page or delta whose content the server is missing.</summary>
public sealed record SyncNeedPayload(string Repo, IReadOnlyList<string> Paths);
