using AiChromeProxy.Client.Tree;

namespace AiChromeProxy.Client.Shell;

/// <summary>
/// The centre tabs: the permanent Welcome tab, the Errors tab, the Project settings tab, one Conflict tab per conflicted file and one tab per opened file (its id is <see cref="FileTab"/>
/// of the path, so a file named like a built-in tab never takes its place).
/// </summary>
public sealed class TabSet
{
	public const string Welcome = ":welcome";
	public const string Errors = ":errors";
	public const string Settings = ":settings";

	private const string FilePrefix = "file:";
	private const string ConflictPrefix = "conflict:";
	private const string ChatPrefix = "chat:";

	private readonly List<string> _open = [Welcome];

	/// <summary>Open tabs, in strip order (Welcome first).</summary>
	public IReadOnlyList<string> Open => _open;

	public string Active { get; private set; } = Welcome;

	public static string FileTab(string path) => FilePrefix + path;

	public static bool IsFile(string id) => id.StartsWith(FilePrefix, StringComparison.Ordinal);

	/// <summary>The file path of a file tab; null for the built-in tabs.</summary>
	public static string? PathOf(string id) => IsFile(id) ? id[FilePrefix.Length..] : null;

	public static string ConflictTab(string path) => ConflictPrefix + path;

	public static bool IsConflict(string id) => id.StartsWith(ConflictPrefix, StringComparison.Ordinal);

	/// <summary>The file path of a conflict tab; null for any other tab.</summary>
	public static string? ConflictPath(string id) => IsConflict(id) ? id[ConflictPrefix.Length..] : null;

	/// <summary>The tab of a chat session; <c>null</c> is the tab of a new chat (nothing sent yet).</summary>
	public static string ChatTab(string? sessionId) => ChatPrefix + sessionId;

	public static bool IsChat(string id) => id.StartsWith(ChatPrefix, StringComparison.Ordinal);

	/// <summary>The session of a chat tab; null for the tab of a new chat and for any other tab.</summary>
	public static string? ChatSessionId(string id) => IsChat(id) && id.Length > ChatPrefix.Length ? id[ChatPrefix.Length..] : null;

	/// <summary>The file a tab is about (a file tab or a conflict tab), highlighted in the tree; null for the others.</summary>
	public static string? SelectedPath(string id) => PathOf(id) ?? ConflictPath(id);

	/// <summary>Closes the conflict tabs whose path is not in <paramref name="conflicted"/> any more.</summary>
	public void CloseResolved(IReadOnlyCollection<string> conflicted)
	{
		foreach (var id in _open.Where(id => ConflictPath(id) is { } path && !conflicted.Contains(path)).ToList())
		{
			Close(id);
		}
	}

	/// <summary>Focuses the tab, opening it at the end of the strip first when needed.</summary>
	public void Show(string id)
	{
		if (!_open.Contains(id))
		{
			_open.Add(id);
		}

		Active = id;
	}

	/// <summary>The tab of a new chat becomes the tab of the session its first message created (same place in the strip); when that tab is open already, it takes the focus.</summary>
	public void Rename(string oldId, string newId)
	{
		var index = _open.IndexOf(oldId);
		if (index < 0)
		{
			return;
		}

		if (_open.Contains(newId))
		{
			Close(oldId);
			Show(newId);
			return;
		}

		_open[index] = newId;
		if (Active == oldId)
		{
			Active = newId;
		}
	}

	/// <summary>The file tabs of a renamed file (or of the files in a renamed folder) take the new path; order and the active tab are kept.</summary>
	public void FollowRename(string oldPath, string newPath, bool isFolder)
	{
		var indexes = Enumerable.Range(0, _open.Count).Where(i => IsFile(_open[i])).ToList();
		var mapped = TabPaths.AfterRename(indexes.Select(i => PathOf(_open[i])!), oldPath, newPath, isFolder);
		for (var n = 0; n < indexes.Count; n++)
		{
			var id = FileTab(mapped[n]);
			if (Active == _open[indexes[n]])
			{
				Active = id;
			}

			_open[indexes[n]] = id;
		}
	}

	/// <summary>Closes the file tabs of a deleted file (or of the files in a deleted folder).</summary>
	public void CloseDeleted(string path, bool isFolder)
	{
		foreach (var closed in TabPaths.ToClose(_open.Where(IsFile).Select(id => PathOf(id)!).ToList(), path, isFolder))
		{
			Close(FileTab(closed));
		}
	}

	/// <summary>Closes a tab (Welcome stays); closing the active one activates the tab that takes its place, else the previous one.</summary>
	public void Close(string id)
	{
		var index = _open.IndexOf(id);
		if (id == Welcome || index < 0)
		{
			return;
		}

		_open.RemoveAt(index);
		if (Active == id)
		{
			Active = _open[Math.Min(index, _open.Count - 1)];
		}
	}

	/// <summary>WAI-ARIA tabs with automatic activation: Left/Right move (wrapping), Home/End jump.</summary>
	public void OnKey(string key)
	{
		var index = _open.IndexOf(Active);
		Active = key switch
		{
			"ArrowRight" => _open[(index + 1) % _open.Count],
			"ArrowLeft" => _open[(index - 1 + _open.Count) % _open.Count],
			"Home" => _open[0],
			"End" => _open[^1],
			_ => Active,
		};
	}
}
