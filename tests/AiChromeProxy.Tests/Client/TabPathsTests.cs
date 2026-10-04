using AiChromeProxy.Client.Tree;

namespace AiChromeProxy.Tests.Client;

public sealed class TabPathsTests
{
	[Fact]
	public void AfterRename_AFileTabFollowsItsNewPath()
	{
		var tabs = TabPaths.AfterRename(["src/a.cs", "src/b.cs"], "src/a.cs", "src/c.cs", false);

		Assert.Equal(["src/c.cs", "src/b.cs"], tabs);
	}

	[Fact]
	public void AfterRename_FilesInsideARenamedFolderFollow_ButSimilarNamesDoNot()
	{
		var tabs = TabPaths.AfterRename(["src/a.cs", "src/App/b.cs", "src2/c.cs", "other/src/d.cs"], "src", "lib", true);

		Assert.Equal(["lib/a.cs", "lib/App/b.cs", "src2/c.cs", "other/src/d.cs"], tabs);
	}

	[Fact]
	public void AfterRename_ANestedFolderRenameKeepsTheRest()
	{
		var tabs = TabPaths.AfterRename(["src/App/a.cs", "src/b.cs"], "src/App", "src/Core", true);

		Assert.Equal(["src/Core/a.cs", "src/b.cs"], tabs);
	}

	[Fact]
	public void AfterRename_ACaseOnlyRenameMapsToTheNewCase()
	{
		Assert.Equal(["A.cs"], TabPaths.AfterRename(["a.cs"], "a.cs", "A.cs", false));
		Assert.Equal(["Src/a.cs"], TabPaths.AfterRename(["src/a.cs"], "src", "Src", true));
	}

	[Fact]
	public void AfterRename_AFileRenameLeavesAFolderOfTheSameNameAlone() =>
		Assert.Equal(["a/x.cs"], TabPaths.AfterRename(["a/x.cs"], "a", "b", false));

	[Fact]
	public void ToClose_ADeletedFileClosesItsTab() =>
		Assert.Equal(["src/a.cs"], TabPaths.ToClose(["src/a.cs", "src/b.cs"], "src/a.cs", false));

	[Fact]
	public void ToClose_ADeletedFolderClosesEverythingInside() =>
		Assert.Equal(["src/a.cs", "src/App/b.cs"], TabPaths.ToClose(["src/a.cs", "src/App/b.cs", "src2/c.cs", "x.cs"], "src", true));
}
