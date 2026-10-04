namespace AiChromeProxy.Client.Tree;

/// <summary>Text of the delete confirmation.</summary>
public sealed record DeleteDialog(string Title, string Body)
{
	public const string Warning = "This cannot be undone. The server's copy is deleted at the next sync.";

	/// <param name="path">Path of the item.</param>
	/// <param name="isFolder">Whether it is a folder.</param>
	/// <param name="fileCount">Files inside a folder (every file, excluded ones too); ignored for a file.</param>
	public static DeleteDialog For(string path, bool isFolder, int fileCount) =>
		new(
			isFolder ? $"Delete the folder `{path}` and its {fileCount} {(fileCount == 1 ? "file" : "files")}?" : $"Delete `{path}`?",
			Warning);
}
