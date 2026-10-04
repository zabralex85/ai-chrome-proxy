using AiChromeProxy.Client.Tree;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

/// <summary>
/// The file tree's actions: they change the picked folder in the browser (the user's truth) and wake the scan, so the mirror follows as
/// ordinary client changes: a create is an upload, a delete a delete, a rename a delete of the old path plus an upload of the new one.
/// Nothing here throws to the UI; every action returns a <see cref="TreeActionResult"/>.
/// </summary>
public sealed partial class SyncEngine
{
	public const string ResolveFirst = "Resolve the server change first";
	public const string ExcludedNote = "Created; excluded from sync";
	public const string RenamedExcludedNote = "Renamed; excluded from sync";

	/// <summary>Said after a delete that leaves nothing to sync; also the cycle's problem then (instead of <c>LooksEmpty</c>).</summary>
	public const string KeepsLastFiles = "The server keeps the last synced files while the folder has none to sync.";

	private const string WriteRefused = "Write access to the folder was not granted; nothing was changed.";
	private const string RootRefused = "The picked folder itself cannot be changed.";
	private const string Busy = "Sync is busy; try again in a moment.";
	private const string FolderChanged = "Another folder was picked; nothing was changed.";

	/// <summary>How long a tree action waits for a running sync cycle before it gives up.</summary>
	private static readonly TimeSpan CycleWait = TimeSpan.FromSeconds(15);

	/// <summary>The last successful tree action was a delete: an empty folder is then no sign of lost access.</summary>
	private bool _deletedLast;

	private IReadOnlyList<string> _directories = [];

	/// <summary>
	/// The folders of the picked folder that sync does not exclude (empty ones too), sorted by path. A new list only when a scan finds a
	/// different set; the explorer builds its tree from <see cref="Files"/> and these.
	/// </summary>
	public IReadOnlyList<string> Directories => _directories;

	/// <summary>Creates an empty file in <paramref name="parent"/> (empty for the picked folder). Call it straight from the click.</summary>
	public Task<TreeActionResult> CreateFileAsync(string parent, string name) => CreateAsync(parent, name, isFolder: false);

	/// <summary>Creates a folder in <paramref name="parent"/> (empty for the picked folder). Call it straight from the click.</summary>
	public Task<TreeActionResult> CreateFolderAsync(string parent, string name) => CreateAsync(parent, name, isFolder: true);

	/// <summary>Renames a file or folder within its folder. Call it straight from the click.</summary>
	public Task<TreeActionResult> RenameAsync(string path, string newName)
	{
		if (path.Length == 0)
		{
			return Task.FromResult(new TreeActionResult(false, RootRefused));
		}

		var parent = ParentOf(path);
		var check = TreeNames.Check(parent, newName, Siblings(parent).Where(s => s != NameOf(path)), _rules, isFolder: !_fileIndex.ContainsKey(path));
		if (check.Error is not null)
		{
			return Task.FromResult(new TreeActionResult(false, check.Error));
		}

		var target = parent.Length == 0 ? newName : parent + "/" + newName;
		return ActAsync(
			$"rename '{path}'",
			[path, target],
			() => folder.RenameAsync(path, newName),
			SyncActivityKind.TreeAction,
			check.Excluded ? $"Renamed '{path}' to '{target}'; excluded from sync." : $"Renamed '{path}' to '{target}'.",
			check.Excluded ? RenamedExcludedNote : null);
	}

	/// <summary>Deletes a file, or a folder with everything in it (permanent). Call it straight from the click, after the user confirmed.</summary>
	public Task<TreeActionResult> DeleteAsync(string path, bool isFolder) =>
		path.Length == 0
			? Task.FromResult(new TreeActionResult(false, RootRefused))
			: DeleteCoreAsync(path, isFolder);

	/// <summary>How many files are inside a folder as it is now, excluded ones too (for the delete confirmation); null when it cannot be counted.</summary>
	public async Task<FileCount?> CountFilesAsync(string path)
	{
		try
		{
			return await folder.CountFilesAsync(path, MaxScanEntries);
		}
		catch (JSException)
		{
			return null;
		}
	}

	/// <summary>Whether a server change waits (or conflicts) for the path or, for a folder, for a file inside it: the menu then disables Rename and Delete.</summary>
	public bool HasServerChange(string path) => RemoteTouches(path);

	/// <summary>Whether the browser can rename folders (the menu disables Rename on folders when not).</summary>
	public Task<bool> CanRenameFoldersAsync() => folder.CanRenameFoldersAsync();

	private static string ParentOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;

	private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

	private async Task<TreeActionResult> DeleteCoreAsync(string path, bool isFolder)
	{
		var last = NothingLeftWithout(path);
		var result = await ActAsync($"delete '{path}'", [path], () => folder.DeleteAsync(path, isFolder), SyncActivityKind.TreeAction, $"Deleted '{path}'.");
		if (result.Ok && last)
		{
			_deletedLast = true;
			Log(SyncActivityKind.TreeAction, KeepsLastFiles);
			return result with { Note = KeepsLastFiles };
		}

		return result;
	}

	private Task<TreeActionResult> CreateAsync(string parent, string name, bool isFolder)
	{
		var check = TreeNames.Check(parent, name, Siblings(parent), _rules, isFolder);
		if (check.Error is not null)
		{
			return Task.FromResult(new TreeActionResult(false, check.Error));
		}

		var path = parent.Length == 0 ? name : parent + "/" + name;
		return ActAsync(
			$"create '{path}'",
			[path],
			() => isFolder ? folder.CreateFolderAsync(path) : folder.CreateFileAsync(path),
			SyncActivityKind.TreeAction,
			check.Excluded ? $"Created '{path}'; excluded from sync." : $"Created '{path}'.",
			check.Excluded ? ExcludedNote : null);
	}

	/// <summary>Whether no file to sync is left once <paramref name="path"/> (a file, or a folder with everything in it) is gone, going by the last scan.</summary>
	private bool NothingLeftWithout(string path) =>
		!_files.Any(f => f.Path != path && !f.Path.StartsWith(path + "/", StringComparison.Ordinal) && f.State != FileSyncState.TooLarge && f.State != FileSyncState.Error);

	/// <summary>A path changed here: its failed upload (and those under it) is not remembered, so the same content coming back is tried at once.</summary>
	private void ForgetFailures(string path)
	{
		foreach (var key in _failures.Keys.Where(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
		{
			_failures.Remove(key);
		}
	}

	/// <summary>The names directly inside <paramref name="parent"/> (files and folders), for the duplicate check.</summary>
	private IEnumerable<string> Siblings(string parent)
	{
		var prefix = parent.Length == 0 ? string.Empty : parent + "/";
		return _files.Select(f => f.Path).Concat(_directories)
			.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.Length > prefix.Length)
			.Select(p => p[prefix.Length..].Split('/')[0])
			.Distinct(StringComparer.Ordinal);
	}

	/// <summary>A server change waits (or conflicts) for the path itself or, for a folder, for a file inside it.</summary>
	private bool RemoteTouches(string path) =>
		_remote.Keys.Any(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Checks the paths against the server's waiting changes, asks for write access when needed (that is the first await, so the click's user
	/// activation is still valid), then takes the cycle guard (waiting up to <see cref="CycleWait"/> for a running cycle), checks again,
	/// acts, logs, and wakes the scan: no server write lands between the steps of an action, and no scan sees it half-way.
	/// </summary>
	private async Task<TreeActionResult> ActAsync(string what, string[] paths, Func<Task> act, SyncActivityKind kind, string logged, string? note = null)
	{
		if (paths.Any(RemoteTouches))
		{
			return new TreeActionResult(false, ResolveFirst);
		}

		var generation = _generation;

		// CanWrite may be stale (no awaited permission check before the prompt, which would use up the click's user activation): a failed write re-reads it below.
		if (!CanWrite)
		{
			var logged0 = _activityCount;
			await AllowWritingAsync();
			if (!CanWrite)
			{
				// A thrown permission request already logged why (ClickFailed): one entry per failure.
				if (_activityCount == logged0)
				{
					Log(SyncActivityKind.Error, WriteRefused);
				}

				return new TreeActionResult(false, WriteRefused);
			}
		}

		if (!await EnterCycleAsync(CycleWait))
		{
			return new TreeActionResult(false, Busy);
		}

		try
		{
			// While waiting, another folder may have been picked or a server change may have arrived (pushed, or applied by the cycle waited for).
			if (generation != _generation)
			{
				return new TreeActionResult(false, FolderChanged);
			}

			if (paths.Any(RemoteTouches))
			{
				return new TreeActionResult(false, ResolveFirst);
			}

			try
			{
				await act();
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				if (ex is JSException)
				{
					CanWrite = await folder.HasWriteAccessAsync();
				}

				var error = ex.Message.Split('\n')[0].Trim();
				Log(SyncActivityKind.Error, $"Could not {what}: {error}");
				return new TreeActionResult(false, error);
			}

			foreach (var touched in paths)
			{
				ForgetFailures(touched);
			}

			_deletedLast = false;
			Log(kind, logged);
			return new TreeActionResult(true, null, note);
		}
		finally
		{
			Volatile.Write(ref _cycleRunning, 0);
			Raise();
			Wake();
		}
	}
}
