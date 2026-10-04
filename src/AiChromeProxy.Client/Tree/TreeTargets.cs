namespace AiChromeProxy.Client.Tree;

/// <summary>Where a tree action applies, from the clicked node (null or empty path: the empty part of the tree, i.e. the picked folder).</summary>
public static class TreeTargets
{
	public const string FolderRenameUnsupported = "Your browser cannot rename folders";

	public const string ServerChangePending = "Resolve the server change first";

	/// <summary>The folder New File / New Folder create in: the clicked folder itself, the folder of a clicked file, or the root (empty).</summary>
	public static string NewItemFolder(string? clickedPath, bool isFolder)
	{
		if (string.IsNullOrEmpty(clickedPath))
		{
			return string.Empty;
		}

		if (isFolder)
		{
			return clickedPath;
		}

		var slash = clickedPath.LastIndexOf('/');
		return slash < 0 ? string.Empty : clickedPath[..slash];
	}

	/// <param name="serverChangePending">A server change waits or conflicts for the path (or something under it).</param>
	public static MenuState CanRename(string? path, bool isFolder, bool canRenameFolders, bool serverChangePending)
	{
		if (string.IsNullOrEmpty(path))
		{
			return new MenuState(false);
		}

		if (isFolder && !canRenameFolders)
		{
			return new MenuState(false, FolderRenameUnsupported);
		}

		return serverChangePending ? new MenuState(false, ServerChangePending) : new MenuState(true);
	}

	/// <param name="serverChangePending">A server change waits or conflicts for the path (or something under it).</param>
	public static MenuState CanDelete(string? path, bool serverChangePending)
	{
		if (string.IsNullOrEmpty(path))
		{
			return new MenuState(false);
		}

		return serverChangePending ? new MenuState(false, ServerChangePending) : new MenuState(true);
	}
}
