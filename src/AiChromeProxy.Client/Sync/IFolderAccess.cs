namespace AiChromeProxy.Client.Sync;

/// <summary>The picked folder in the browser (File System Access API, read-only); <see cref="JsFolderAccess"/> in the app, a fake in tests.</summary>
public interface IFolderAccess
{
	/// <summary>Shows the folder picker; the folder name, or null when the user cancelled.</summary>
	Task<string?> PickAsync();

	/// <summary>The folder picked on an earlier visit, or null.</summary>
	Task<FolderGrant?> RestoreAsync();

	/// <summary>Asks the browser for read access again (must run from a click).</summary>
	Task<bool> RequestAccessAsync();

	/// <summary>
	/// Walks the folder without descending into <paramref name="skipDirectories"/>; stops after <paramref name="maxEntries"/> entries
	/// (files and folders). Entries that cannot be read are left out and reported in <see cref="FolderScan.Skipped"/>;
	/// throws when the folder itself cannot be listed (it never looks empty instead).
	/// </summary>
	Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries);

	/// <summary>SHA-256 (lower-case hex) per path of the last scan, in order; null for a file that could not be read or is larger than <c>SyncLimits.MaxFileSize</c>.</summary>
	Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths);

	/// <summary>Text of a file of the last scan, or null when it is not there; throws when it is there but cannot be read.</summary>
	Task<string?> ReadTextAsync(string path);

	/// <summary>
	/// Bytes [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>) of a file of the last scan (fewer at its end).
	/// Offset 0 snapshots the file for the following chunks; fails when the path is not in the last scan or the file changed since.
	/// </summary>
	Task<byte[]> ReadChunkAsync(string path, long offset, int length);

	/// <summary>Calls <paramref name="changed"/> with true when the tab becomes visible or gets focus, false when it is hidden.</summary>
	Task WatchVisibilityAsync(Action<bool> changed);
}
