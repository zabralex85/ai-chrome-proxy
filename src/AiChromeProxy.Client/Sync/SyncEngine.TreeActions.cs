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

	private const string WriteRefused = "Write access to the folder was not granted; nothing was changed.";
	private const string RootRefused = "The picked folder itself cannot be changed.";

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
		var check = TreeNames.Check(parent, newName, Siblings(parent).Where(s => s != NameOf(path)));
		if (check.Error is not null)
		{
			return Task.FromResult(new TreeActionResult(false, check.Error));
		}

		var target = parent.Length == 0 ? newName : parent + "/" + newName;
		return ActAsync($"rename '{path}'", [path, target], () => folder.RenameAsync(path, newName), SyncActivityKind.TreeAction, $"Renamed '{path}' to '{target}'.");
	}

	/// <summary>Deletes a file, or a folder with everything in it (permanent). Call it straight from the click, after the user confirmed.</summary>
	public Task<TreeActionResult> DeleteAsync(string path, bool isFolder) =>
		path.Length == 0
			? Task.FromResult(new TreeActionResult(false, RootRefused))
			: ActAsync($"delete '{path}'", [path], () => folder.DeleteAsync(path, isFolder), SyncActivityKind.Deleted, $"Deleted '{path}'.");

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

	private static string ParentOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;

	private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

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
	/// activation is still valid), acts, logs and wakes the scan. ponytail: runs beside a cycle, not inside its guard: a scan that overlaps sees
	/// the folder half-way and the next one (woken here) corrects it; take <c>ExclusiveAsync</c>'s guard if that ever shows.
	/// </summary>
	private async Task<TreeActionResult> ActAsync(string what, string[] paths, Func<Task> act, SyncActivityKind kind, string logged, string? note = null)
	{
		if (paths.Any(RemoteTouches))
		{
			return new TreeActionResult(false, ResolveFirst);
		}

		if (!CanWrite)
		{
			await AllowWritingAsync();
			if (!CanWrite)
			{
				Log(SyncActivityKind.Error, WriteRefused);
				return new TreeActionResult(false, WriteRefused);
			}
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
			Raise();
			return new TreeActionResult(false, error);
		}

		Log(kind, logged);
		Raise();
		Wake();
		return new TreeActionResult(true, null, note);
	}
}
