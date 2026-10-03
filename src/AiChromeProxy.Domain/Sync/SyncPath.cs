namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// Rules every path in the sync protocol must meet, on both ends: relative, <c>/</c>-separated, no empty / <c>.</c> / <c>..</c> segments,
/// nothing Windows cannot store or would reinterpret. The server additionally checks that the resolved path stays inside the repo folder.
/// </summary>
public static class SyncPath
{
	public const int MaxLength = 260;

	/// <summary>Suffix of the server's temporary upload files; reserved so a synced file can never collide with one.</summary>
	public const string TempSuffix = ".aicp-tmp";

	private static readonly char[] Forbidden = ['\\', ':', '*', '?', '"', '<', '>', '|'];

	private static readonly HashSet<string> ReservedNames = new(
		["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
		StringComparer.OrdinalIgnoreCase);

	public static bool IsValid(string? path) => GetError(path) is null;

	/// <returns>Null when <paramref name="path"/> is acceptable; otherwise why not (safe to show: it quotes nothing but the path).</returns>
	public static string? GetError(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return "Path is empty.";
		}

		if (path.Length > MaxLength)
		{
			return $"Path is longer than {MaxLength} characters.";
		}

		if (path.IndexOfAny(Forbidden) >= 0 || path.Any(char.IsControl))
		{
			return $"Path '{path}' contains a character that is not allowed (\\ : * ? \" < > | or a control character).";
		}

		if (path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
		{
			return $"Path '{path}' uses the reserved suffix {TempSuffix}.";
		}

		foreach (var segment in path.Split('/'))
		{
			if (segment is "" or "." or "..")
			{
				return $"Path '{path}' must be relative, without empty, '.' or '..' segments.";
			}

			if (segment.EndsWith('.') || segment.EndsWith(' '))
			{
				return $"Path '{path}' has a segment ending with a dot or a space.";
			}

			var stem = segment.Split('.')[0].TrimEnd(' ');
			if (ReservedNames.Contains(stem))
			{
				return $"Path '{path}' uses the reserved Windows name '{stem}'.";
			}
		}

		return null;
	}
}
