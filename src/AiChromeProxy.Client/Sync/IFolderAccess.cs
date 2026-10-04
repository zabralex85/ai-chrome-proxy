namespace AiChromeProxy.Client.Sync;

/// <summary>The picked folder in the browser (File System Access API; read access, write access only once granted); <see cref="JsFolderAccess"/> in the app, a fake in tests.</summary>
public interface IFolderAccess
{
	/// <summary>Whether this browser can open folders at all: a secure context with the File System Access API (desktop Chrome, Edge).</summary>
	Task<bool> IsSupportedAsync();

	/// <summary>Shows the folder picker; the folder name, or null when the user cancelled.</summary>
	Task<string?> PickAsync();

	/// <summary>The folder picked on an earlier visit, or null.</summary>
	Task<FolderGrant?> RestoreAsync();

	/// <summary>Whether the picked folder is still readable (the browser's permission; asks nothing, changes nothing).</summary>
	Task<bool> HasAccessAsync();

	/// <summary>Asks the browser for read access again (must run from a click).</summary>
	Task<bool> RequestAccessAsync();

	/// <summary>
	/// Walks the folder without descending into <paramref name="skipDirectories"/> (a name: at any depth; <c>/a/b</c>: only that
	/// path; case-insensitive, see <see cref="IgnoreRules.SkipDirectories"/>); stops after <paramref name="maxEntries"/> entries
	/// (files and folders). Entries that cannot be read are left out and reported in <see cref="FolderScan.Skipped"/>;
	/// throws when the folder itself cannot be listed (it never looks empty instead).
	/// </summary>
	Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries);

	/// <summary>SHA-256 (lower-case hex) per path of the last scan, in order; null for a file that could not be read or is larger than <c>SyncLimits.MaxFileSize</c>.</summary>
	Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths);

	/// <summary>Text of a file in the folder (read now, not from the last scan), or null when it is not there; throws when it is there but cannot be read.</summary>
	Task<string?> ReadTextAsync(string path);

	/// <summary>The file as it is now (not from the last scan), at most <c>FileText.MaxBytes + 1</c> bytes of it; null when it is not there; throws when it is there but cannot be read (or the path is invalid).</summary>
	Task<FileBytes?> ReadFileAsync(string path);

	/// <summary>
	/// Bytes [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>) of a file of the last scan (fewer at its end).
	/// Offset 0 snapshots the file for the following chunks; fails when the path is not in the last scan or the file changed since.
	/// </summary>
	Task<byte[]> ReadChunkAsync(string path, long offset, int length);

	/// <summary>SHA-256 of the file as it is now (not from the last scan); null when there is no such file. Throws when it cannot be read.</summary>
	Task<string?> HashNowAsync(string path);

	/// <summary>Whether the folder may be written (queryPermission readwrite; asks nothing).</summary>
	Task<bool> HasWriteAccessAsync();

	/// <summary>Asks for write access (must run from a click).</summary>
	Task<bool> RequestWriteAccessAsync();

	/// <summary>Replaces (or creates, with its folders) the file atomically (createWritable).</summary>
	Task WriteAsync(string path, byte[] content);

	/// <summary>
	/// Deletes the file, or with <paramref name="recursive"/> the folder and everything inside it (a folder without it throws);
	/// no-op when it is not there. The picked folder itself is never deleted (an empty path is invalid).
	/// </summary>
	Task DeleteAsync(string path, bool recursive = false);

	/// <summary>Creates an empty file in an existing folder; throws when the name is taken (by a file or a folder) or the path is invalid.</summary>
	Task CreateFileAsync(string path);

	/// <summary>Creates a folder in an existing folder; throws when the name is taken or the path is invalid.</summary>
	Task CreateFolderAsync(string path);

	/// <summary>
	/// Renames the file or folder to <paramref name="newName"/> (a single name) in the same folder; never overwrites (a taken name, ignoring case, throws),
	/// never renames the picked folder. A file without browser <c>move</c> support is copied and the old one removed (up to <c>SyncLimits.MaxFileSize</c>);
	/// a folder needs <see cref="CanRenameFoldersAsync"/>. A change of case only works.
	/// </summary>
	Task RenameAsync(string path, string newName);

	/// <summary>Whether this browser can rename folders (<c>FileSystemHandle.move</c>).</summary>
	Task<bool> CanRenameFoldersAsync();

	/// <summary>Every file under the folder as it is now, excluded ones too; stops after <paramref name="maxEntries"/> entries (files and folders) like <see cref="ScanAsync"/>. Throws when the folder is not there.</summary>
	Task<int> CountFilesAsync(string path, int maxEntries);

	/// <summary>Calls <paramref name="changed"/> with true when the tab becomes visible or gets focus, false when it is hidden.</summary>
	Task WatchVisibilityAsync(Action<bool> changed);
}
