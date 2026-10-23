namespace AiChromeProxy.Domain.Projects;

/// <summary><c>mirror.files {repo, files, next}</c>: server → client, files sorted by path (ordinal); <paramref name="Next"/> is the <c>after</c> of the next page, null on the last.</summary>
public sealed record MirrorFilesPayload(string Repo, IReadOnlyList<MirrorFile> Files, string? Next);
