namespace AiChromeProxy.Client.Sync;

/// <summary>Result of a folder walk: every file found (paths <c>/</c>-separated) and whether the walk stopped at the entry limit.</summary>
public sealed record FolderScan(IReadOnlyList<FileMeta> Files, bool Truncated);
