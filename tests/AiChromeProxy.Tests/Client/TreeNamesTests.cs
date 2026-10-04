using AiChromeProxy.Client.Tree;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class TreeNamesTests
{
	[Theory]
	[InlineData("", "a.cs", null)]
	[InlineData("src", "a.cs", null)]
	[InlineData("src/App", "Readme", null)]
	[InlineData("", "", "Enter a name.")]
	[InlineData("", "   ", "Enter a name.")]
	[InlineData("", "a/b", "A name cannot contain / or \\.")]
	[InlineData("", "a\\b", "A name cannot contain / or \\.")]
	public void Check_ReportsEmptyNamesAndSeparators(string folder, string name, string? expected) =>
		Assert.Equal(expected, TreeNames.Check(folder, name, []).Error);

	[Theory]
	[InlineData("..")]
	[InlineData(".")]
	[InlineData("CON")]
	[InlineData("com1.txt")]
	[InlineData("a.")]
	[InlineData("a ")]
	[InlineData("a:b")]
	[InlineData(".git")]
	[InlineData("GIT~1")]
	public void Check_UsesTheSyncPathRules(string name)
	{
		var error = TreeNames.Check("src", name, []).Error;

		Assert.NotNull(error);
		Assert.Equal(SyncPath.GetError("src/" + name), error);
	}

	[Fact]
	public void Check_RefusesASiblingWithTheSameNameIgnoringCase()
	{
		var check = TreeNames.Check("src", "readme.MD", ["a.cs", "README.md"]);

		Assert.Equal("'readme.MD' already exists here.", check.Error);
	}

	[Fact]
	public void Check_ChecksTheNameAtTheRootToo() =>
		Assert.Equal(SyncPath.GetError("CON"), TreeNames.Check(string.Empty, "CON", []).Error);

	[Theory]
	[InlineData(".env", false, true)]
	[InlineData("bin", true, true)]
	[InlineData("obj", true, true)]
	[InlineData("a.cs", false, false)]
	[InlineData("src", true, false)]
	public void Check_FlagsNamesSyncWouldExclude_ButAllowsThem(string name, bool isFolder, bool excluded)
	{
		var check = TreeNames.Check(string.Empty, name, [], IgnoreRules.Create(null), isFolder);

		Assert.Null(check.Error);
		Assert.Equal(excluded, check.Excluded);
	}

	[Fact]
	public void Check_UsesTheGitignoreRules() =>
		Assert.True(TreeNames.Check("src", "x.log", [], IgnoreRules.Create("*.log")).Excluded);

	[Fact]
	public void Check_WithoutRules_NothingIsExcluded() =>
		Assert.False(TreeNames.Check(string.Empty, ".env", []).Excluded);

	[Fact]
	public void Check_AnInvalidNameIsNeverFlaggedExcluded() =>
		Assert.False(TreeNames.Check(string.Empty, "a.", [], IgnoreRules.Create("a.")).Excluded);
}
