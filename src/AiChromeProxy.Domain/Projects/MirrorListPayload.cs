namespace AiChromeProxy.Domain.Projects;

/// <summary><c>mirror.list {repo, after}</c>: client → server, one page of the repo's mirror files after the path <paramref name="After"/> (null: from the start); reply <c>mirror.files</c>.</summary>
public sealed record MirrorListPayload(string Repo, string? After = null);
