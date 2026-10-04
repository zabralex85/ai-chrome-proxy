using AiChromeProxy.Client.Shell;

namespace AiChromeProxy.Tests.Client;

public sealed class FileTreeDirectoriesTests
{
	[Fact]
	public void Build_ShowsAnEmptyFolder()
	{
		var tree = FileTree.Build(["a.cs"], ["empty"]);

		Assert.Equal(["empty", "a.cs"], tree.Select(n => n.Name));
		Assert.True(tree[0].IsFolder);
		Assert.Empty(tree[0].Children);
	}

	[Fact]
	public void Build_ShowsNestedEmptyFolders_AndKeepsTheirFiles()
	{
		var tree = FileTree.Build(["src/a.cs"], ["src", "src/App", "src/App/Empty"]);

		var src = Assert.Single(tree);
		Assert.Equal(["App", "a.cs"], src.Children.Select(n => n.Name));
		var empty = Assert.Single(Assert.Single(src.Children, c => c.IsFolder).Children);
		Assert.Equal("src/App/Empty", empty.Path);
		Assert.True(empty.IsFolder);
	}

	[Fact]
	public void Build_AFolderListedTwiceAppearsOnce()
	{
		var tree = FileTree.Build(["src/a.cs"], ["src", "src"]);

		Assert.Single(tree);
	}

	[Fact]
	public void Build_WithoutDirectories_IsUnchanged() =>
		Assert.Equal(["src", "b.txt"], FileTree.Build(["b.txt", "src/a.cs"]).Select(n => n.Name));

	[Fact]
	public void Build_AnEmptyFolderSitsBesideFolderNodesFromFiles()
	{
		var tree = FileTree.Build(["a/x"], ["b"]);

		Assert.Equal(["a", "b"], tree.Select(n => n.Name));
	}
}
