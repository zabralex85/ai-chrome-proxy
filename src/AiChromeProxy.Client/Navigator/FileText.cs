using System.Text;

namespace AiChromeProxy.Client.Navigator;

/// <summary>Decides whether a file's bytes can be shown as text in the viewer.</summary>
public static class FileText
{
	/// <summary>The view limit: 5 MB. (The 20 MB sync limit is separate: Monaco has to stay responsive.)</summary>
	public const int MaxBytes = 5 * 1024 * 1024;

	/// <summary>A NUL in this many leading bytes makes a file binary.</summary>
	public const int BinaryProbeBytes = 8192;

	/// <summary>Decodes UTF-8 (BOM stripped) or UTF-16 with a BOM; anything with a NUL early on (UTF-8 with a BOM included), a UTF-32 BOM, or that is not valid UTF-8 or UTF-16, is binary.</summary>
	/// <param name="bytes">The file's content.</param>
	/// <returns>The text, or why there is none.</returns>
	public static FileTextResult Decode(byte[] bytes) => Decode(bytes, bytes.Length);

	/// <summary>Like <see cref="Decode(byte[])"/> for leading bytes of a file whose real size is <paramref name="size"/> (a file over <see cref="MaxBytes"/> is read only up to <c>MaxBytes + 1</c> bytes).</summary>
	/// <param name="bytes">The file's content, or its first <c>MaxBytes + 1</c> bytes.</param>
	/// <param name="size">The file's size in bytes.</param>
	/// <returns>The text, or why there is none.</returns>
	public static FileTextResult Decode(byte[] bytes, long size)
	{
		if (size > MaxBytes || bytes.Length > MaxBytes)
		{
			return new FileTextResult(null, FileTextReason.TooLarge, Math.Max(size, bytes.Length));
		}

		try
		{
			if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()))
			{
				return new FileTextResult(null, FileTextReason.Binary, size);
			}

			if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
			{
				return Text(new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2), size);
			}

			if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
			{
				return Text(new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2), size);
			}

			if (bytes.AsSpan(0, Math.Min(bytes.Length, BinaryProbeBytes)).Contains((byte)0))
			{
				return new FileTextResult(null, FileTextReason.Binary, size);
			}

			var utf8 = new UTF8Encoding(false, true);
			return bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
				? Text(utf8.GetString(bytes, 3, bytes.Length - 3), size)
				: Text(utf8.GetString(bytes), size);
		}
		catch (ArgumentException)
		{
			return new FileTextResult(null, FileTextReason.Binary, size);
		}
	}

	private static FileTextResult Text(string text, long size) => new(text, FileTextReason.None, size);
}
