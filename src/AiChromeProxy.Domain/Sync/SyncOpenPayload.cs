namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.open {repo}</c>: the picked folder's name; the reply <c>sync.opened {repo, settings?}</c> carries the sanitized name used from then on and the project settings.</summary>
public sealed record SyncOpenPayload(string Repo, ProjectSettings? Settings = null);
