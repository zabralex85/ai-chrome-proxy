using AiChromeProxy.Client.Shell;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

/// <summary>The logic behind the chat UI: chat tabs, the status text and the permission/model fields of the project settings.</summary>
public sealed class ChatUiTests
{
	[Fact]
	public void ChatTab_IsPerSession_AndNeverAFileOrConflictTab()
	{
		var id = TabSet.ChatTab("s1");

		Assert.True(TabSet.IsChat(id));
		Assert.Equal("s1", TabSet.ChatSessionId(id));
		Assert.False(TabSet.IsFile(id));
		Assert.False(TabSet.IsConflict(id));
		Assert.Null(TabSet.SelectedPath(id));
		Assert.NotEqual(TabSet.ChatTab("s2"), id);
	}

	[Fact]
	public void ChatTab_ForADraft_IsTheNewChatTab()
	{
		var id = TabSet.ChatTab(null);

		Assert.True(TabSet.IsChat(id));
		Assert.Null(TabSet.ChatSessionId(id));
		Assert.NotEqual(TabSet.ChatTab("s1"), id);
		Assert.False(TabSet.IsChat(TabSet.FileTab("chat:s1")));
		Assert.False(TabSet.IsChat(TabSet.Settings));
	}

	[Fact]
	public void Rename_TheDraftTabBecomesTheSessionsTab_InPlace()
	{
		var tabs = new TabSet();
		tabs.Show(TabSet.ChatTab(null));
		tabs.Show(TabSet.FileTab("a.cs"));
		tabs.Show(TabSet.ChatTab(null));

		tabs.Rename(TabSet.ChatTab(null), TabSet.ChatTab("s1"));

		Assert.Equal([TabSet.Welcome, TabSet.ChatTab("s1"), TabSet.FileTab("a.cs")], tabs.Open);
		Assert.Equal(TabSet.ChatTab("s1"), tabs.Active);
	}

	[Fact]
	public void Rename_WhenTheSessionsTabIsOpenAlready_JustFocusesIt()
	{
		var tabs = new TabSet();
		tabs.Show(TabSet.ChatTab("s1"));
		tabs.Show(TabSet.ChatTab(null));

		tabs.Rename(TabSet.ChatTab(null), TabSet.ChatTab("s1"));

		Assert.Equal([TabSet.Welcome, TabSet.ChatTab("s1")], tabs.Open);
		Assert.Equal(TabSet.ChatTab("s1"), tabs.Active);
	}

	[Fact]
	public void Rename_OfATabThatIsNotOpen_DoesNothing()
	{
		var tabs = new TabSet();

		tabs.Rename(TabSet.ChatTab(null), TabSet.ChatTab("s1"));

		Assert.Equal([TabSet.Welcome], tabs.Open);
	}

	[Theory]
	[InlineData(false, null, "Claude idle")]
	[InlineData(true, 0, "Claude working… 0:00")]
	[InlineData(true, 42, "Claude working… 0:42")]
	[InlineData(true, 754, "Claude working… 12:34")]
	[InlineData(true, 3723, "Claude working… 1:02:03")]
	public void AgentStatus_IdleOrWorkingWithElapsedTime(bool running, int? seconds, string expected)
	{
		Assert.Equal(expected, Format.AgentStatus(running, seconds is { } s ? TimeSpan.FromSeconds(s) : null));
	}

	[Fact]
	public void AgentStatus_RunningWithoutAClockYet_ShowsZero()
	{
		Assert.Equal("Claude working… 0:00", Format.AgentStatus(true, null));
	}

	[Theory]
	[InlineData(0.12, "$0.12")]
	[InlineData(0.0123, "$0.01")]
	[InlineData(1234.5, "$1234.50")]
	[InlineData(0, "$0.00")]
	public void Cost_TwoDecimalsWhateverTheCulture(double cost, string expected)
	{
		Assert.Equal(expected, Format.Cost((decimal)cost));
	}

	[Fact]
	public void Settings_DefaultToAskAndNoModel_AndAreNotDirty()
	{
		var form = new ProjectSettingsForm(ProjectSettings.Default);

		Assert.Equal(("ask", string.Empty), (form.Permissions, form.Model));
		Assert.False(form.IsDirty);
	}

	[Fact]
	public void Settings_ReadWhatWasSaved()
	{
		var form = new ProjectSettingsForm(new ProjectSettings { AgentPermissions = "settings", AgentModel = "opus" });

		Assert.Equal(("settings", "opus"), (form.Permissions, form.Model));
		Assert.False(form.IsDirty);
	}

	[Theory]
	[InlineData("all", "")]
	[InlineData("ask", "sonnet")]
	public void Settings_ChangingPermissionsOrModel_IsDirty(string permissions, string model)
	{
		var form = new ProjectSettingsForm(ProjectSettings.Default) { Permissions = permissions, Model = model };

		Assert.True(form.IsDirty);
	}

	[Fact]
	public void Settings_ToSettings_KeepsTheOtherKeysAndTheAllowedTools()
	{
		var saved = new ProjectSettings { Excludes = "*.log", AgentAllowedTools = ["Bash(ls)"] };
		var form = new ProjectSettingsForm(saved) { Permissions = "all", Model = "  opus  " };

		var settings = form.ToSettings();

		Assert.Equal("all", settings.AgentPermissions);
		Assert.Equal("opus", settings.AgentModel);
		Assert.Equal("*.log", settings.Excludes);
		Assert.Equal(["Bash(ls)"], settings.AgentAllowedTools);
	}

	[Fact]
	public void Settings_ToSettings_AskAndEmptyModelAreTheDefaults()
	{
		var saved = new ProjectSettings { AgentPermissions = "all", AgentModel = "opus" };
		var form = new ProjectSettingsForm(saved) { Permissions = "ask", Model = " " };

		var settings = form.ToSettings();

		Assert.Null(settings.AgentPermissions);
		Assert.Null(settings.AgentModel);
	}
}
