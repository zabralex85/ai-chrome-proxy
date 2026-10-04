namespace AiChromeProxy.Client.Navigator;

/// <summary>Why a file's content is not shown in the viewer.</summary>
public enum FileTextReason
{
	/// <summary>The content is text and is shown.</summary>
	None,

	/// <summary>NUL in the first bytes, or not valid UTF-8.</summary>
	Binary,

	/// <summary>Over <see cref="FileText.MaxBytes"/>.</summary>
	TooLarge,
}

/// <summary>The outcome of <see cref="FileText.Decode"/>: the text, or the reason there is none.</summary>
/// <param name="Text">The decoded text without a BOM; null when <paramref name="Reason"/> is not <see cref="FileTextReason.None"/>.</param>
/// <param name="Reason">Why there is no text.</param>
/// <param name="Size">The file's size in bytes.</param>
public sealed record FileTextResult(string? Text, FileTextReason Reason, long Size)
{
	/// <summary>Gets the note shown instead of the content (empty for text).</summary>
	public string Message => Reason switch
	{
		FileTextReason.Binary => "Binary file — not shown",
		FileTextReason.TooLarge => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Too large to show ({Size / (1024.0 * 1024.0):0.0} MB)"),
		_ => string.Empty,
	};
}
