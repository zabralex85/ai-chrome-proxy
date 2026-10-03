namespace AiChromeProxy.Domain.Sync;

/// <summary><c>project.settings.get/set</c> and the reply <c>project.settings</c>: the settings of one repo (null in a get).</summary>
public sealed record ProjectSettingsPayload(string Repo, ProjectSettings? Settings = null);
