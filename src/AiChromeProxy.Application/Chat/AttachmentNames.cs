using System.Buffers;

namespace AiChromeProxy.Application.Chat;

/// <summary>Makes a file name from the protocol safe to store on Windows.</summary>
public static class AttachmentNames
{
	/// <summary>Longest stored name, in UTF-16 characters.</summary>
	public const int MaxChars = 100;

	private const string PartSuffix = ".part";

	/// <summary>Longest extension kept when a name is cut.</summary>
	private const int MaxExtensionChars = 20;

	private static readonly SearchValues<char> Invalid = SearchValues.Create("<>:\"/\\|?*");

	private static readonly HashSet<string> Reserved = new(
		["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
		StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The last path segment; invalid and control characters → <c>_</c>; spaces trimmed and trailing dots dropped; reserved device names
	/// (the part before the first dot) prefixed with <c>_</c>; a name ending in <c>.part</c> gets a trailing <c>_</c> (the store's temp suffix);
	/// at most <see cref="MaxChars"/> characters, keeping the extension and never cutting a surrogate pair; <c>file</c> when nothing is left.
	/// </summary>
	public static string Sanitize(string? name)
	{
		var last = (name ?? string.Empty).Split('/', '\\')[^1];
		var clean = string.Concat(last.Select(c => char.IsControl(c) || Invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
		if (clean.Length == 0)
		{
			return "file";
		}

		if (Reserved.Contains(clean.Split('.')[0]))
		{
			clean = "_" + clean;
		}

		if (clean.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
		{
			clean += "_";
		}

		if (clean.Length > MaxChars)
		{
			var extension = Path.GetExtension(clean);
			extension = extension.Length > MaxExtensionChars ? string.Empty : extension;
			var keep = MaxChars - extension.Length;
			if (char.IsHighSurrogate(clean[keep - 1]))
			{
				keep--;
			}

			clean = clean[..keep].TrimEnd('.', ' ') + extension;
		}

		return clean;
	}
}
