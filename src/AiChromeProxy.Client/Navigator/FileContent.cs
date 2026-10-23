using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Client.Navigator;

/// <summary>What a file tab shows for the bytes just read from the folder.</summary>
public static class FileContent
{
	/// <summary>The note of a file that is not in the folder (any more).</summary>
	public const string Missing = "This file is no longer in the folder";

	/// <summary>The note of a mirror file that is not on the server (any more).</summary>
	public const string MissingOnServer = "This file is no longer on the server";

	/// <summary>Decides between the text and a note.</summary>
	/// <param name="bytes">What <see cref="IFolderAccess.ReadFileAsync"/> (or the mirror) returned: null for a missing file.</param>
	/// <param name="missing">The note for a missing file.</param>
	/// <returns>The text to show, or the note shown instead (exactly one of them is set).</returns>
	public static (string? Text, string? Note) Decide(FileBytes? bytes, string missing = Missing)
	{
		if (bytes is null)
		{
			return (null, missing);
		}

		var decoded = FileText.Decode(bytes.Bytes, bytes.Size);
		return decoded.Text is { } text ? (text, null) : (null, decoded.Message);
	}
}
