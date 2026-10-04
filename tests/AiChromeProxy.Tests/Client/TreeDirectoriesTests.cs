using AiChromeProxy.Client.Tree;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class TreeDirectoriesTests
{
	[Fact]
	public void Visible_HidesFoldersTheRulesExclude_AndTheOnesInsideThem() =>
		Assert.Equal(["src", "src/App"], TreeDirectories.Visible(["src", "src/App", "gen", "gen/x", "src/tmp.out"], IgnoreRules.Create("gen*\n*.out/")));

	[Fact]
	public void Visible_WithoutRules_KeepsAll() =>
		Assert.Equal(["bin"], TreeDirectories.Visible(["bin"], null));
}
