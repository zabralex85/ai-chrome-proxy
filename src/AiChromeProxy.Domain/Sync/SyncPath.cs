using System.Buffers;
using System.Globalization;
using System.Text;

namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// Rules every path in the sync protocol must meet, on both ends: relative, <c>/</c>-separated, no empty / <c>.</c> / <c>..</c> segments,
/// no <c>.git</c> segment, nothing Windows cannot store or would reinterpret (device names, 8.3 short names, best-fit lookalikes of <c>/ \ . :</c>). The server additionally checks that the resolved path stays inside the repo folder.
/// </summary>
public static class SyncPath
{
	public const int MaxLength = 260;

	/// <summary>Characters of the per-session tag in the server's temporary file names, <c>&lt;name&gt;.&lt;tag&gt;.aicp-tmp</c>.</summary>
	public const int TempTagLength = 8;

	/// <summary>255 (NTFS name limit) minus <c>.&lt;tag&gt;</c> and <see cref="TempSuffix"/>, so the server's temporary file name always fits.</summary>
	public const int MaxSegmentLength = 237;

	/// <summary>Suffix of the server's temporary upload files; reserved so a synced file can never collide with one.</summary>
	public const string TempSuffix = ".aicp-tmp";

	private const string GitSegment = ".git";

	private static readonly char[] Forbidden = ['\\', ':', '*', '?', '"', '<', '>', '|'];

	/// <summary>Fullwidth / division / fraction slashes, fullwidth reverse solidus, full stop and colon: Windows' best-fit ANSI conversion turns them into <c>/ \ . :</c>.</summary>
	private static readonly HashSet<int> Lookalikes = [0xFF0F, 0xFF3C, 0x2215, 0xFF0E, 0xFF1A, 0x2044];

	private static readonly HashSet<string> ReservedNames = new(
		[
			"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
			"COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
			"LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
		],
		StringComparer.OrdinalIgnoreCase);

	public static bool IsValid(string? path) => GetError(path) is null;

	/// <summary>
	/// Checks an entry of the <c>keep</c> list of <c>sync.manifest</c>: a file path, or a folder prefix ending in <c>/</c> checked by its segments.
	/// </summary>
	/// <returns>Null when acceptable; otherwise why not.</returns>
	public static string? GetKeepError(string? keep) => GetError(keep is not null && keep.EndsWith('/') ? keep[..^1] : keep);

	/// <summary>Whether <paramref name="keep"/> lists <paramref name="path"/> or a folder prefix (ending in <c>/</c>) of it; the set's comparer decides about case.</summary>
	public static bool IsKept(string path, IReadOnlySet<string> keep)
	{
		if (keep.Contains(path))
		{
			return true;
		}

		for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
		{
			if (keep.Contains(path[..(slash + 1)]))
			{
				return true;
			}
		}

		return false;
	}

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

		if (!IsPlainText(path))
		{
			return "Path contains an unpaired surrogate, an invisible format character or a lookalike of / \\ . or :.";
		}

		foreach (var segment in path.Split('/'))
		{
			if (segment is "" or "." or "..")
			{
				return $"Path '{path}' must be relative, without empty, '.' or '..' segments.";
			}

			if (segment.Length > MaxSegmentLength)
			{
				return $"Path '{path}' has a segment longer than {MaxSegmentLength} characters.";
			}

			if (segment.EndsWith('.') || segment.EndsWith(' '))
			{
				return $"Path '{path}' has a segment ending with a dot or a space.";
			}

			// Git metadata (a .git folder, or a submodule's .git file) is never synced: writing it on the server could make git run hooks.
			if (segment.Equals(GitSegment, StringComparison.OrdinalIgnoreCase))
			{
				return $"Path '{path}' has a '{GitSegment}' segment; git metadata is never synced.";
			}

			if (segment.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return $"Path '{path}' uses the reserved suffix {TempSuffix}.";
			}

			// On a volume with 8.3 names "GIT~1" opens ".git"; such a name never needs to be synced.
			if (segment.Zip(segment.Skip(1)).Any(p => p.First == '~' && char.IsAsciiDigit(p.Second)))
			{
				return $"Path '{path}' has a segment that looks like an 8.3 short name (~ followed by a digit).";
			}

			var stem = segment.Split('.')[0].TrimEnd(' ');
			if (ReservedNames.Contains(stem))
			{
				return $"Path '{path}' uses the reserved Windows name '{stem}'.";
			}
		}

		return null;
	}

	private static bool IsPlainText(ReadOnlySpan<char> path)
	{
		while (!path.IsEmpty)
		{
			if (Rune.DecodeFromUtf16(path, out var rune, out var used) != OperationStatus.Done
				|| Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
				|| Lookalikes.Contains(rune.Value))
			{
				return false;
			}

			path = path[used..];
		}

		return true;
	}
}
