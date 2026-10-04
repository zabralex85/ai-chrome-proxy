namespace AiChromeProxy.Client.Tree;

/// <summary>The tree's context menu: its items, keyboard moves, keyboard shortcuts and where it goes on screen.</summary>
public static class TreeMenu
{
	public const string NewFile = "new-file";
	public const string NewFolder = "new-folder";
	public const string Rename = "rename";
	public const string Delete = "delete";

	/// <summary>Menu width and height used to keep it on screen (CSS makes it about this large).</summary>
	public const int Width = 200;

	public const int Height = 140;

	/// <summary>
	/// The items for a click on <paramref name="path"/> (null: the empty part of the tree, which offers the two creates only). Write access is not
	/// a reason to disable anything: the action asks for it.
	/// </summary>
	/// <param name="serverChangePending">A server change waits or conflicts for the path (or something under it).</param>
	public static IReadOnlyList<TreeMenuItem> Items(string? path, bool isFolder, bool canRenameFolders, bool serverChangePending)
	{
		List<TreeMenuItem> items =
		[
			new(NewFile, "New File", "file-code", null, new MenuState(true)),
			new(NewFolder, "New Folder", "folder", null, new MenuState(true)),
		];
		if (!string.IsNullOrEmpty(path))
		{
			items.Add(new(Rename, "Rename", "edit", "F2", TreeTargets.CanRename(path, isFolder, canRenameFolders, serverChangePending)));
			items.Add(new(Delete, "Delete", "trash", "Del", TreeTargets.CanDelete(path, serverChangePending)));
		}

		return items;
	}

	/// <summary>The item that takes the focus after <paramref name="key"/> (arrows wrap, Home/End jump); disabled items stay reachable so their tooltip can be read.</summary>
	public static int Move(string key, int index, int count) => count == 0
		? 0
		: key switch
		{
			"ArrowDown" => (index + 1) % count,
			"ArrowUp" => (index - 1 + count) % count,
			"Home" => 0,
			"End" => count - 1,
			_ => index,
		};

	/// <summary>Shift+F10 and the Menu key open the menu, F2 renames, Delete deletes (the focused row).</summary>
	public static TreeKeyAction KeyAction(string key, bool shift) => key switch
	{
		"ContextMenu" => TreeKeyAction.OpenMenu,
		"F10" when shift => TreeKeyAction.OpenMenu,
		"F2" => TreeKeyAction.Rename,
		"Delete" => TreeKeyAction.Delete,
		_ => TreeKeyAction.None,
	};

	/// <summary>The top-left corner for a menu opened at a point, moved back when it would run off the viewport (viewport 0: unknown, no move).</summary>
	public static (int X, int Y) Place(double x, double y, int viewportWidth, int viewportHeight)
	{
		var left = viewportWidth > 0 ? Math.Min(x, viewportWidth - Width) : x;
		var top = viewportHeight > 0 ? Math.Min(y, viewportHeight - Height) : y;
		return ((int)Math.Max(0, left), (int)Math.Max(0, top));
	}
}
