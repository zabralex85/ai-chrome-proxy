namespace AiChromeProxy.Client.Navigator;

/// <summary>Finds the lines of a changed file to mark in the open viewer.</summary>
public static class LineDiff
{
	/// <summary>Files over this many lines mark only the first and last differing lines.</summary>
	public const int MaxLines = 20_000;

	/// <summary>The most cells of the LCS table (4 bytes each): 16 MB.</summary>
	private const long MaxCells = 4_000_000;

	/// <summary>
	/// The 1-based lines of <paramref name="newText"/> that were inserted or changed. Trims the common prefix and suffix, then runs an LCS over the middle;
	/// for a file over <see cref="MaxLines"/> lines, or a middle whose table would exceed <see cref="MaxCells"/>, only the first and last differing lines are marked.
	/// </summary>
	/// <param name="oldText">The text shown so far.</param>
	/// <param name="newText">The text just read.</param>
	/// <returns>The line numbers, ascending.</returns>
	public static IReadOnlyList<int> ChangedLines(string oldText, string newText)
	{
		var a = Split(oldText);
		var b = Split(newText);
		var prefix = 0;
		while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
		{
			prefix++;
		}

		var suffix = 0;
		while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
		{
			suffix++;
		}

		var n = a.Length - prefix - suffix;
		var m = b.Length - prefix - suffix;
		if (m == 0)
		{
			return [];
		}

		if (a.Length > MaxLines || b.Length > MaxLines || (long)(n + 1) * (m + 1) > MaxCells)
		{
			return m == 1 ? [prefix + 1] : [prefix + 1, prefix + m];
		}

		if (n == 0)
		{
			return [.. Enumerable.Range(prefix + 1, m)];
		}

		return Lcs(a, b, prefix, n, m);
	}

	private static int[] Lcs(string[] a, string[] b, int offset, int n, int m)
	{
		var width = m + 1;
		var table = new int[(n + 1) * width];
		for (var i = n - 1; i >= 0; i--)
		{
			for (var j = m - 1; j >= 0; j--)
			{
				table[(i * width) + j] = a[offset + i] == b[offset + j]
					? table[((i + 1) * width) + j + 1] + 1
					: Math.Max(table[((i + 1) * width) + j], table[(i * width) + j + 1]);
			}
		}

		var changed = new List<int>();
		int x = 0, y = 0;
		while (y < m)
		{
			if (x < n && a[offset + x] == b[offset + y])
			{
				x++;
				y++;
			}
			else if (x < n && table[((x + 1) * width) + y] >= table[(x * width) + y + 1])
			{
				x++;
			}
			else
			{
				changed.Add(offset + y + 1);
				y++;
			}
		}

		return [.. changed];
	}

	private static string[] Split(string text)
	{
		var lines = text.Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			lines[i] = lines[i].TrimEnd('\r');
		}

		return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
	}
}
