using AiChromeProxy.Client.Shell;

namespace AiChromeProxy.Tests.Client;

public sealed class TabSetTreeTests
{
	[Fact]
	public void FollowRename_AFile_TheTabKeepsItsPlaceAndStaysActive()
	{
		var tabs = Open("a.txt", "b.txt", "c.txt");
		tabs.Show(TabSet.FileTab("b.txt"));

		tabs.FollowRename("b.txt", "B.txt", false);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("a.txt"), TabSet.FileTab("B.txt"), TabSet.FileTab("c.txt")], tabs.Open);
		Assert.Equal(TabSet.FileTab("B.txt"), tabs.Active);
	}

	[Fact]
	public void FollowRename_AFolder_TabsInsideTakeTheNewPrefix()
	{
		var tabs = Open("src/a.cs", "srcx/b.cs", "src/deep/c.cs");

		tabs.FollowRename("src", "lib", true);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("lib/a.cs"), TabSet.FileTab("srcx/b.cs"), TabSet.FileTab("lib/deep/c.cs")], tabs.Open);
		Assert.Equal(TabSet.FileTab("lib/deep/c.cs"), tabs.Active);
	}

	[Fact]
	public void FollowRename_LeavesOtherTabsAlone()
	{
		var tabs = Open("a.txt");
		tabs.Show(TabSet.Errors);

		tabs.FollowRename("a.txt", "b.txt", false);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("b.txt"), TabSet.Errors], tabs.Open);
		Assert.Equal(TabSet.Errors, tabs.Active);
	}

	[Fact]
	public void FollowRename_OntoAStaleFileTab_ClosesIt_NoDuplicateIds()
	{
		var tabs = Open("a.txt", "b.txt", "c.txt");

		tabs.FollowRename("a.txt", "b.txt", false);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("b.txt"), TabSet.FileTab("c.txt")], tabs.Open);
		Assert.Equal(TabSet.FileTab("c.txt"), tabs.Active);
	}

	[Fact]
	public void FollowRename_ActiveStaleTargetTab_TheRenamedTabTakesTheFocus()
	{
		var tabs = Open("b.txt", "a.txt");
		tabs.Show(TabSet.FileTab("a.txt"));

		tabs.FollowRename("a.txt", "b.txt", false);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("b.txt")], tabs.Open);
		Assert.Equal(TabSet.FileTab("b.txt"), tabs.Active);
	}

	[Fact]
	public void FollowRename_AFolderOntoStaleTabs_ClosesTheTargetsTabs_NoDuplicateIds()
	{
		var tabs = Open("lib", "lib/a.cs", "src/a.cs", "lib/old.cs", "src/b.cs");

		tabs.FollowRename("src", "lib", true);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("lib/a.cs"), TabSet.FileTab("lib/b.cs")], tabs.Open);
		Assert.Equal(TabSet.FileTab("lib/b.cs"), tabs.Active);
		Assert.Equal(tabs.Open.Count, tabs.Open.Distinct().Count());
	}

	[Fact]
	public void CloseDeleted_AFile_ClosesOnlyThatTab()
	{
		var tabs = Open("a.txt", "b.txt");

		tabs.CloseDeleted("a.txt", false);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("b.txt")], tabs.Open);
	}

	[Fact]
	public void CloseDeleted_AFolder_ClosesEverythingInside()
	{
		var tabs = Open("src/a.cs", "src/deep/b.cs", "srcx/c.cs", "d.cs");

		tabs.CloseDeleted("src", true);

		Assert.Equal([TabSet.Welcome, TabSet.FileTab("srcx/c.cs"), TabSet.FileTab("d.cs")], tabs.Open);
		Assert.Equal(TabSet.FileTab("d.cs"), tabs.Active);
	}

	private static TabSet Open(params string[] paths)
	{
		var tabs = new TabSet();
		foreach (var path in paths)
		{
			tabs.Show(TabSet.FileTab(path));
		}

		return tabs;
	}
}
