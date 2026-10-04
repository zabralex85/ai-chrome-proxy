using AiChromeProxy.Client.Tree;

namespace AiChromeProxy.Tests.Client;

public sealed class TreeMenuTests
{
	[Fact]
	public void Items_ForARow_AreTheFourActionsInOrder()
	{
		var items = TreeMenu.Items("src/a.cs", false, true, false);

		Assert.Equal(["New File", "New Folder", "Rename", "Delete"], items.Select(i => i.Label));
		Assert.Equal(["new-file", "new-folder", "rename", "delete"], items.Select(i => i.Id));
		Assert.Equal([null, null, "F2", "Del"], items.Select(i => i.Shortcut));
		Assert.All(items, i => Assert.True(i.State.Enabled));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Items_ForTheEmptyPartOfTheTree_OnlyCreate(string? path) =>
		Assert.Equal(["New File", "New Folder"], TreeMenu.Items(path, false, true, false).Select(i => i.Label));

	[Fact]
	public void Items_FolderWithoutBrowserSupport_RenameIsDisabledWithTheReason()
	{
		var rename = TreeMenu.Items("src", true, false, false).Single(i => i.Id == TreeMenu.Rename);

		Assert.False(rename.State.Enabled);
		Assert.Equal("Your browser cannot rename folders", rename.State.Reason);
	}

	[Fact]
	public void Items_ServerChangePending_RenameAndDeleteAreDisabledWithTheReason()
	{
		var items = TreeMenu.Items("a.cs", false, true, true);

		Assert.All(items.Where(i => i.Id is TreeMenu.Rename or TreeMenu.Delete), i =>
		{
			Assert.False(i.State.Enabled);
			Assert.Equal("Resolve the server change first", i.State.Reason);
		});
		Assert.All(items.Where(i => i.Id is TreeMenu.NewFile or TreeMenu.NewFolder), i => Assert.True(i.State.Enabled));
	}

	[Theory]
	[InlineData("ArrowDown", 0, 4, 1)]
	[InlineData("ArrowDown", 3, 4, 0)]
	[InlineData("ArrowUp", 0, 4, 3)]
	[InlineData("ArrowUp", 2, 4, 1)]
	[InlineData("Home", 2, 4, 0)]
	[InlineData("End", 1, 4, 3)]
	[InlineData("x", 2, 4, 2)]
	[InlineData("ArrowDown", 0, 0, 0)]
	public void Move_ArrowsWrapAndHomeEndJump(string key, int index, int count, int expected) =>
		Assert.Equal(expected, TreeMenu.Move(key, index, count));

	[Theory]
	[InlineData("ContextMenu", false, TreeKeyAction.OpenMenu)]
	[InlineData("F10", true, TreeKeyAction.OpenMenu)]
	[InlineData("F10", false, TreeKeyAction.None)]
	[InlineData("F2", false, TreeKeyAction.Rename)]
	[InlineData("Delete", false, TreeKeyAction.Delete)]
	[InlineData("Enter", false, TreeKeyAction.None)]
	public void KeyAction_ShiftF10MenuF2AndDelete(string key, bool shift, TreeKeyAction expected) =>
		Assert.Equal(expected, TreeMenu.KeyAction(key, shift));

	[Theory]
	[InlineData(10, 20, 1000, 800, 10, 20)]
	[InlineData(950, 20, 1000, 800, 800, 20)]
	[InlineData(10, 790, 1000, 800, 10, 660)]
	[InlineData(950, 790, 0, 0, 950, 790)]
	[InlineData(-5, -5, 1000, 800, 0, 0)]
	public void Place_KeepsTheMenuOnScreen(double x, double y, int width, int height, int left, int top) =>
		Assert.Equal((left, top), TreeMenu.Place(x, y, width, height));
}
