using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Projects;
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Domain.Projects;

namespace AiChromeProxy.Tests.Client;

public sealed class ProjectsUiTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Line_ThisFolderWorkingOnlineAndTime()
	{
		Assert.Equal("this folder · 5 min ago", ProjectLine.Of(new RepoInfo("My_Repo", Now.AddMinutes(-5), false, true), "my_repo", Now));
		Assert.Equal("Claude is working · online · 42 s ago", ProjectLine.Of(new RepoInfo("Other", Now.AddSeconds(-42), true, true), "My_Repo", Now));
		Assert.Equal("no chat yet", ProjectLine.Of(new RepoInfo("Other", null, false, false), null, Now));
	}

	[Theory]
	[InlineData("ArrowDown", -1, 3, 0)]
	[InlineData("ArrowDown", 2, 3, 0)]
	[InlineData("ArrowUp", 0, 3, 2)]
	[InlineData("ArrowUp", -1, 3, 2)]
	[InlineData("Home", 2, 3, 0)]
	[InlineData("End", 0, 3, 2)]
	[InlineData("x", 1, 3, 1)]
	[InlineData("ArrowDown", -1, 0, -1)]
	public void Move_WrapsAround(string key, int index, int count, int expected) =>
		Assert.Equal(expected, ProjectLine.Move(key, index, count));

	[Fact]
	public void Placeholder_ReadOnlyWins()
	{
		Assert.Equal(ChatEngine.ReadOnlyNote, Format.ChatPlaceholder(true, true, readOnly: true));
		Assert.Equal("Message Claude. Enter sends, Shift+Enter adds a line.", Format.ChatPlaceholder(true, true));
	}
}
