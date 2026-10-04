using AiChromeProxy.Client.Tree;

namespace AiChromeProxy.Tests.Client;

public sealed class DeleteDialogTests
{
	private const string Body = "This cannot be undone. The server's copy is deleted at the next sync.";

	[Fact]
	public void For_AFile_AsksAboutThePath()
	{
		var dialog = DeleteDialog.For("src/a.cs", false, 0);

		Assert.Equal("Delete `src/a.cs`?", dialog.Title);
		Assert.Equal(Body, dialog.Body);
	}

	[Fact]
	public void For_AFolder_CountsItsFiles() =>
		Assert.Equal("Delete the folder `src` and its 12 files?", DeleteDialog.For("src", true, 12).Title);

	[Fact]
	public void For_AFolderWithOneFile_UsesTheSingular() =>
		Assert.Equal("Delete the folder `src` and its 1 file?", DeleteDialog.For("src", true, 1).Title);

	[Fact]
	public void ForUncounted_AsksAboutTheFolderAndEverythingInIt()
	{
		var dialog = DeleteDialog.ForUncounted("src");

		Assert.Equal("Delete the folder `src` and everything in it?", dialog.Title);
		Assert.Equal(Body, dialog.Body);
	}

	[Fact]
	public void For_AFolderCountedOnlyPartly_SaysMoreThan() =>
		Assert.Equal("Delete the folder `src` and more than 20000 files?", DeleteDialog.For("src", true, 20000, true).Title);

	[Fact]
	public void For_AnEmptyFolder_SaysZeroFiles()
	{
		var dialog = DeleteDialog.For("src", true, 0);

		Assert.Equal("Delete the folder `src` and its 0 files?", dialog.Title);
		Assert.Equal(Body, dialog.Body);
	}
}
