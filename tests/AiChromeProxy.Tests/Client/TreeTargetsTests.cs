using AiChromeProxy.Client.Tree;

namespace AiChromeProxy.Tests.Client;

public sealed class TreeTargetsTests
{
	[Theory]
	[InlineData(null, false, "")]
	[InlineData("", true, "")]
	[InlineData("src", true, "src")]
	[InlineData("src/App", true, "src/App")]
	[InlineData("a.cs", false, "")]
	[InlineData("src/a.cs", false, "src")]
	[InlineData("src/App/a.cs", false, "src/App")]
	public void NewItemFolder_IsTheFolderItselfTheFilesFolderOrTheRoot(string? clicked, bool isFolder, string expected) =>
		Assert.Equal(expected, TreeTargets.NewItemFolder(clicked, isFolder));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void RenameAndDelete_NeverApplyToTheRoot(string? root)
	{
		Assert.False(TreeTargets.CanRename(root, true, true, false).Enabled);
		Assert.False(TreeTargets.CanDelete(root, false).Enabled);
	}

	[Fact]
	public void CanRename_AFileWorksWithoutFolderSupport() =>
		Assert.True(TreeTargets.CanRename("a.cs", false, false, false).Enabled);

	[Fact]
	public void CanRename_AFolderNeedsBrowserSupport()
	{
		Assert.True(TreeTargets.CanRename("src", true, true, false).Enabled);

		var state = TreeTargets.CanRename("src", true, false, false);

		Assert.False(state.Enabled);
		Assert.Equal("Your browser cannot rename folders", state.Reason);
	}

	[Fact]
	public void RenameAndDelete_WaitForAServerChange()
	{
		var rename = TreeTargets.CanRename("a.cs", false, true, true);
		var delete = TreeTargets.CanDelete("a.cs", true);

		Assert.False(rename.Enabled);
		Assert.Equal("Resolve the server change first", rename.Reason);
		Assert.False(delete.Enabled);
		Assert.Equal("Resolve the server change first", delete.Reason);
	}

	[Fact]
	public void CanDelete_AnItemIsEnabledWithoutAReason()
	{
		var state = TreeTargets.CanDelete("src", false);

		Assert.True(state.Enabled);
		Assert.Null(state.Reason);
	}
}
