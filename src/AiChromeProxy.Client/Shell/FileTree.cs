namespace AiChromeProxy.Client.Shell;

/// <summary>Explorer tree logic: build from the flat file list, the rows currently visible, keyboard navigation.</summary>
public static class FileTree
{
	/// <summary>Folders first, then files; each group sorted by name ignoring case.</summary>
	/// <param name="paths">File paths, <c>/</c>-separated.</param>
	public static IReadOnlyList<TreeNode> Build(IEnumerable<string> paths) =>
		Build(paths.Select(p => (Tail: p, Path: p)).ToList(), string.Empty);

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

	private static List<TreeNode> Build(List<(string Tail, string Path)> items, string prefix)
	{
		var folders = items
			.Where(i => i.Tail.Contains('/'))
			.GroupBy(i => i.Tail[..i.Tail.IndexOf('/')], StringComparer.Ordinal)
			.Select(g => new TreeNode(
				g.Key,
				prefix + g.Key,
				true,
				Build(g.Select(i => (i.Tail[(i.Tail.IndexOf('/') + 1)..], i.Path)).ToList(), prefix + g.Key + "/")))
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
		var files = items
			.Where(i => !i.Tail.Contains('/'))
			.Select(i => new TreeNode(i.Tail, i.Path, false, []))
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
		return [.. folders, .. files];
	}
}
