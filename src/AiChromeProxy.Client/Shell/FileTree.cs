namespace AiChromeProxy.Client.Shell;

/// <summary>Explorer tree logic: build from the flat file list, the rows currently visible, keyboard navigation.</summary>
public static class FileTree
{
	/// <summary>Folders first, then files; each group sorted by name ignoring case.</summary>
	/// <param name="paths">File paths, <c>/</c>-separated.</param>
	/// <param name="directories">Folder paths, so empty folders show too (folders with files appear anyway); null for none.</param>
	public static IReadOnlyList<TreeNode> Build(IEnumerable<string> paths, IEnumerable<string>? directories = null) =>
		Build([.. paths.Select(p => (Tail: p, Path: p, IsFolder: false)), .. (directories ?? []).Select(p => (Tail: p, Path: p, IsFolder: true))], string.Empty);

	/// <summary>Depth-first rows; children only for folders in <paramref name="expanded"/> (folder paths).</summary>
	public static IReadOnlyList<TreeRow> Rows(IReadOnlyList<TreeNode> roots, IReadOnlySet<string> expanded)
	{
		var rows = new List<TreeRow>();
		void Add(IReadOnlyList<TreeNode> nodes, int depth)
		{
			foreach (var node in nodes)
			{
				rows.Add(new TreeRow(node, depth));
				if (node.IsFolder && expanded.Contains(node.Path))
				{
					Add(node.Children, depth + 1);
				}
			}
		}

		Add(roots, 0);
		return rows;
	}

	/// <summary>The row that takes the Tab stop (roving tabindex): the active row while it is visible, else the first row.</summary>
	public static string? TabStop(IReadOnlyList<TreeRow> rows, string? active) =>
		active is not null && rows.Any(r => r.Node.Path == active) ? active : rows.Count > 0 ? rows[0].Node.Path : null;

	/// <summary>
	/// Tree keyboard handling (WAI-ARIA tree pattern): Up/Down move, Home/End jump, Right expands or enters a folder,
	/// Left collapses or goes to the parent, Enter/Space toggles a folder or opens a file. Updates <paramref name="expanded"/>.
	/// </summary>
	/// <returns>The new active row's path, and the file to open (null when none).</returns>
	public static (string? Active, string? Open) OnKey(string key, IReadOnlyList<TreeRow> rows, string? active, ISet<string> expanded)
	{
		if (rows.Count == 0)
		{
			return (null, null);
		}

		var index = Math.Max(0, rows.ToList().FindIndex(r => r.Node.Path == active));
		var row = rows[index];
		switch (key)
		{
			case "ArrowDown":
				return (rows[Math.Min(index + 1, rows.Count - 1)].Node.Path, null);
			case "ArrowUp":
				return (rows[Math.Max(index - 1, 0)].Node.Path, null);
			case "Home":
				return (rows[0].Node.Path, null);
			case "End":
				return (rows[^1].Node.Path, null);
			case "ArrowRight" when row.Node.IsFolder:
				if (expanded.Add(row.Node.Path) || row.Node.Children.Count == 0)
				{
					return (row.Node.Path, null);
				}

				return (row.Node.Children[0].Path, null);
			case "ArrowLeft":
				if (row.Node.IsFolder && expanded.Remove(row.Node.Path))
				{
					return (row.Node.Path, null);
				}

				var parent = rows.Take(index).LastOrDefault(r => r.Depth == row.Depth - 1);
				return (parent?.Node.Path ?? row.Node.Path, null);
			case "Enter" or " ":
				if (!row.Node.IsFolder)
				{
					return (row.Node.Path, row.Node.Path);
				}

				if (!expanded.Remove(row.Node.Path))
				{
					expanded.Add(row.Node.Path);
				}

				return (row.Node.Path, null);
			default:
				return (row.Node.Path, null);
		}
	}

	/// <summary>The row to activate once <paramref name="path"/> is deleted: the next row that is not inside it, else the one before it; null when none is left.</summary>
	public static string? NeighbourAfterDelete(IReadOnlyList<TreeRow> rows, string path)
	{
		var index = rows.ToList().FindIndex(r => r.Node.Path == path);
		if (index < 0)
		{
			return null;
		}

		var next = rows.Skip(index + 1).FirstOrDefault(r => !r.Node.Path.StartsWith(path + "/", StringComparison.Ordinal));
		return (next ?? (index > 0 ? rows[index - 1] : null))?.Node.Path;
	}

	private static List<TreeNode> Build(List<(string Tail, string Path, bool IsFolder)> items, string prefix)
	{
		var folders = items
			.Where(i => i.Tail.Contains('/') || i.IsFolder)
			.GroupBy(i => i.Tail.Contains('/') ? i.Tail[..i.Tail.IndexOf('/')] : i.Tail, StringComparer.Ordinal)
			.Select(g => new TreeNode(
				g.Key,
				prefix + g.Key,
				true,
				Build(g.Where(i => i.Tail.Contains('/')).Select(i => (i.Tail[(i.Tail.IndexOf('/') + 1)..], i.Path, i.IsFolder)).ToList(), prefix + g.Key + "/")))
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
		var files = items
			.Where(i => !i.Tail.Contains('/') && !i.IsFolder)
			.Select(i => new TreeNode(i.Tail, i.Path, false, []))
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
		return [.. folders, .. files];
	}
}
