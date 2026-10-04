namespace AiChromeProxy.Client.Tree;

/// <summary>How open file tabs follow a rename or a delete in the tree.</summary>
public static class TabPaths
{
	/// <summary>The open paths after <paramref name="oldPath"/> became <paramref name="newPath"/> (a file, or a folder prefix); the order is kept.</summary>
	public static IReadOnlyList<string> AfterRename(IEnumerable<string> open, string oldPath, string newPath, bool isFolder) =>
		[.. open.Select(p => Map(p, oldPath, newPath, isFolder))];

	/// <summary>The open paths a delete of <paramref name="path"/> (a file, or a folder with everything inside) closes.</summary>
	public static IReadOnlyList<string> ToClose(IEnumerable<string> open, string path, bool isFolder) =>
		[.. open.Where(p => p == path || (isFolder && IsInside(p, path)))];

	private static string Map(string open, string oldPath, string newPath, bool isFolder)
	{
		if (open == oldPath)
		{
			return newPath;
		}

		return isFolder && IsInside(open, oldPath) ? newPath + open[oldPath.Length..] : open;
	}

	private static bool IsInside(string path, string folder) =>
		path.Length > folder.Length && path.StartsWith(folder, StringComparison.Ordinal) && path[folder.Length] == '/';
}
