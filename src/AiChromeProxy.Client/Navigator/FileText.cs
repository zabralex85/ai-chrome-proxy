using System.Text;

namespace AiChromeProxy.Client.Navigator;

/// <summary>Decides whether a file's bytes can be shown as text in the viewer.</summary>
public static class FileText
{
	/// <summary>The view limit: 5 MB. (The 20 MB sync limit is separate: Monaco has to stay responsive.)</summary>
	public const int MaxBytes = 5 * 1024 * 1024;

	/// <summary>A NUL in this many leading bytes makes a file binary.</summary>
	public const int BinaryProbeBytes = 8192;

	/// <summary>Decodes UTF-8 (BOM stripped) or UTF-16 with a BOM; anything with a NUL early on, or that is not valid UTF-8, is binary.</summary>
	/// <param name="bytes">The file's content.</param>
	/// <returns>The text, or why there is none.</returns>
	public static FileTextResult Decode(byte[] bytes)
	{
		if (bytes.Length > MaxBytes)
		{
			return new FileTextResult(null, FileTextReason.TooLarge, bytes.Length);
		}

		try
		{
			if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
			{
				return Text(new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3), bytes);
			}

			if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
			{
				return Text(new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2), bytes);
			}

			if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
			{
				return Text(new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2), bytes);
			}

			if (bytes.AsSpan(0, Math.Min(bytes.Length, BinaryProbeBytes)).Contains((byte)0))
			{
				return new FileTextResult(null, FileTextReason.Binary, bytes.Length);
			}

			return Text(new UTF8Encoding(false, true).GetString(bytes), bytes);
		}
		catch (ArgumentException)
		{
			return new FileTextResult(null, FileTextReason.Binary, bytes.Length);
		}
	}

	private static FileTextResult Text(string text, byte[] bytes) => new(text, FileTextReason.None, bytes.Length);
}
