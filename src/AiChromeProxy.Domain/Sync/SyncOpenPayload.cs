namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.open {repo}</c>: the picked folder's name; the reply <c>sync.opened {repo}</c> carries the sanitized name used from then on.</summary>
public sealed record SyncOpenPayload(string Repo);
