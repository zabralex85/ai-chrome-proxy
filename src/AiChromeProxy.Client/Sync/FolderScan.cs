namespace AiChromeProxy.Client.Sync;

/// <summary>
/// Result of a folder walk: every file found (paths <c>/</c>-separated), whether the walk stopped at the entry limit, and what it
/// could not see: <see cref="Skipped"/> lists unreadable files by path and unlisted folders as a prefix ending in <c>/</c>.
/// </summary>
public sealed record FolderScan(IReadOnlyList<FileMeta> Files, bool Truncated, IReadOnlyList<string>? Skipped = null);
