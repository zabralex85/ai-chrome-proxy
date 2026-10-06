using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

/// <summary>The Claude tools section of the Project settings tab: what it shows (<see cref="ClaudeToolsView"/>) and the engine's requests against <see cref="LoopbackServer"/>.</summary>
public sealed class ClaudeToolsUiTests : IDisposable
{
	private const string Repo = "My_Repo";

	private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

	private static readonly string OldHash = new('a', 64);

	private static readonly string NewHash = new('b', 64);

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly SyncEngine _sync;
	private readonly ChatEngine _chat;

	public ClaudeToolsUiTests()
	{
		_sync = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		_chat = new ChatEngine(_server.Transport, _sync, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Theory]
	[InlineData("connected", "Connected", "ok")]
	[InlineData("needs-auth", "Needs sign-in", "warn")]
	[InlineData("failed", "Failed", "error")]
	[InlineData("not-configured", "Not configured", "muted")]
	[InlineData("pending", "Waiting for approval", "warn")]
	[InlineData("off", "Off in this project", "muted")]
	[InlineData("missing", "Not installed", "muted")]
	[InlineData("unknown", "Unknown", "muted")]
	[InlineData(null, "Unknown", "muted")]
	public void Badge_TextAndColourPerStatus(string? status, string text, string tone) =>
		Assert.Equal((text, tone), (ClaudeToolsView.Badge(status), ClaudeToolsView.Tone(status)));

	[Fact]
	public void Hint_UnderNeedsSignInAndFailedRowsOnly_CodeBetweenBackticks()
	{
		var form = new ProjectSettingsForm(ProjectSettings.Default);
		Assert.Equal("Sign in on the home computer: run `claude`, then `/mcp`.", ClaudeToolsView.Hint(Server("linear", "needs-auth"), form));
		var failed = ClaudeToolsView.Hint(Server("blender", "failed"), form)!;
		Assert.Equal("Check the server on the home computer: `claude mcp get blender`.", failed);
		Assert.Null(ClaudeToolsView.Hint(Server("github", "connected"), form));
		Assert.Null(ClaudeToolsView.Hint(Pending("team-db", OldHash), form));

		Assert.Equal(
			[("Check the server on the home computer: ", false), ("claude mcp get blender", true), (".", false)],
			ClaudeToolsView.Parts(failed));
	}

	[Fact]
	public void Hint_PendingServerApprovedForAnOlderEntry_ChangedUntilApprovedAgain()
	{
		var form = new ProjectSettingsForm(new ProjectSettings { AgentApprovedMcpServers = [ClaudeToolEntries.Approval("team-db", OldHash), "other"] });
		var teamDb = Pending("team-db", NewHash);

		Assert.False(ClaudeToolsView.IsOn(form, teamDb));
		Assert.Equal("Changed since you approved it — check `.mcp.json` and approve again.", ClaudeToolsView.Hint(teamDb, form));
		Assert.Equal(ClaudeToolsView.ChangedHint, ClaudeToolsView.Hint(Pending("other", NewHash), form));

		ClaudeToolsView.SetOn(form, teamDb, true);

		Assert.Null(ClaudeToolsView.Hint(teamDb, form));
		Assert.Equal(["other", ClaudeToolEntries.Approval("team-db", NewHash)], form.ToSettings().AgentApprovedMcpServers);
	}

	[Fact]
	public void Source_AndName_PluginServersSayTheirPlugin()
	{
		var pluginServer = new ClaudeMcpServerRow("plugin:design:figma", "plugin", "connected", true, "design");

		Assert.Equal(("figma", "plugin design"), (ClaudeToolsView.Name(pluginServer), ClaudeToolsView.Source(pluginServer)));
		Assert.False(ClaudeToolsView.HasSwitch(pluginServer));
		Assert.Equal("claude.ai", ClaudeToolsView.Source(new ClaudeMcpServerRow("claude.ai Docs", "claudeai", "connected", true)));
		Assert.Equal("user", ClaudeToolsView.Source(Server("github", "connected") with { Source = "user" }));
		Assert.Equal("plugin", ClaudeToolsView.Source(Server("plugin:x:y", "connected") with { Source = "plugin" }));
		Assert.Equal(string.Empty, ClaudeToolsView.Source(Server("github", "connected")));
		Assert.Equal("github", ClaudeToolsView.Name(Server("github", "connected")));
		Assert.True(ClaudeToolsView.HasSwitch(Server("github", "connected")));
		Assert.True(ClaudeToolsView.HasSwitch(Pending("team-db", NewHash)));
		Assert.False(ClaudeToolsView.HasSwitch(Server("team-db", "pending")));

		Assert.Equal("design@market · v1.2.0", ClaudeToolsView.Source(new ClaudePluginRow("design@market", "design", "1.2.0", true, true)));
		Assert.Equal("notes@market · off in Claude Code", ClaudeToolsView.Source(new ClaudePluginRow("notes@market", "notes", null, false, true)));
	}

	[Fact]
	public void LastChecked_SaysWhenAndFromWhat_NullWithoutData()
	{
		Assert.Null(ClaudeToolsView.LastChecked(Payload(null, null), Now));
		Assert.Equal("Last checked 5 min ago (from a message)", ClaudeToolsView.LastChecked(Payload(Now.AddMinutes(-5), "run"), Now));
		Assert.Equal("Last checked just now (by Check now)", ClaudeToolsView.LastChecked(Payload(Now, "check"), Now));
		var old = Now.AddDays(-3);
		Assert.Equal($"Last checked {old.ToLocalTime():yyyy-MM-dd HH:mm} (by Check now)", ClaudeToolsView.LastChecked(Payload(old, "check"), Now));
	}

	[Fact]
	public void Switches_EditTheFormsLists_PendingServerMeansApproved()
	{
		var form = new ProjectSettingsForm(new ProjectSettings { AgentDisabledMcpServers = ["blender"] });
		var blender = Server("blender", "off");
		var github = Server("github", "connected");
		var teamDb = Pending("team-db", NewHash);
		var design = new ClaudePluginRow("design@market", "design", "1.0.0", true, true);

		Assert.Equal((false, true, false, true), (ClaudeToolsView.IsOn(form, blender), ClaudeToolsView.IsOn(form, github), ClaudeToolsView.IsOn(form, teamDb), ClaudeToolsView.IsOn(form, design)));
		Assert.False(form.IsDirty);

		ClaudeToolsView.SetOn(form, blender, true);
		ClaudeToolsView.SetOn(form, github, false);
		ClaudeToolsView.SetOn(form, teamDb, true);
		ClaudeToolsView.SetOn(form, design, false);

		Assert.True(form.IsDirty);
		Assert.Equal((true, false, true, false), (ClaudeToolsView.IsOn(form, blender), ClaudeToolsView.IsOn(form, github), ClaudeToolsView.IsOn(form, teamDb), ClaudeToolsView.IsOn(form, design)));
		var settings = form.ToSettings();
		Assert.Equal(["github"], settings.AgentDisabledMcpServers);
		Assert.Equal([ClaudeToolEntries.Approval("team-db", NewHash)], settings.AgentApprovedMcpServers);
		Assert.Equal(["design@market"], settings.AgentDisabledPlugins);

		// Switched back: an unapproved pending server is not disabled; nothing is left to save.
		ClaudeToolsView.SetOn(form, blender, false);
		ClaudeToolsView.SetOn(form, github, true);
		ClaudeToolsView.SetOn(form, teamDb, false);
		ClaudeToolsView.SetOn(form, design, true);
		Assert.False(form.IsDirty);
		Assert.Equal(["blender"], form.ToSettings().AgentDisabledMcpServers);
		Assert.Null(form.ToSettings().AgentApprovedMcpServers);
	}

	[Fact]
	public void Save_ReplacesOnlyTheListsEditedHere()
	{
		var form = new ProjectSettingsForm(new ProjectSettings { AgentDisabledMcpServers = ["a"] });
		form.DisabledPlugins.Add("p@m");

		// Meanwhile another tab switched a server off: untouched here, so kept.
		var saved = form.ToSettings(new ProjectSettings { AgentDisabledMcpServers = ["a", "b"], AgentModel = "opus" });

		Assert.Equal(["a", "b"], saved.AgentDisabledMcpServers);
		Assert.Equal(["p@m"], saved.AgentDisabledPlugins);
		Assert.Equal("opus", saved.AgentModel);

		form.DisabledMcpServers.Clear();
		Assert.Null(form.ToSettings(saved).AgentDisabledMcpServers);
	}

	[Fact]
	public void Error_TextPerFailure()
	{
		Assert.Equal("Open the folder and let it sync first.", ClaudeToolsView.Error(new RequestFailedException(ErrorCodes.BadRequest, "Open the folder and let it sync first.")));
		Assert.Equal("Open a folder first.", ClaudeToolsView.Error(new InvalidOperationException("Open a folder first.")));
		Assert.Equal("No answer from the server; try again.", ClaudeToolsView.Error(new TimeoutException()));
		Assert.Equal("Could not reach the server.", ClaudeToolsView.Error(new TaskCanceledException()));
	}

	[Fact]
	public async Task Tools_NoFolder_Throws()
	{
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.ToolsAsync(check: false));
		Assert.Equal("Open a folder first.", ex.Message);
	}

	[Fact]
	public async Task Tools_NothingRecorded_ThenARunsInitLine_ShowsItsServersFromAMessage()
	{
		await ReadyAsync();
		var empty = await _chat.ToolsAsync(check: false);
		Assert.Equal((0, 0, null), (empty.Servers.Count, empty.Plugins.Count, empty.CheckedAt));

		await _chat.SendAsync("hi");
		var process = await _server.Agent.NextAsync();
		process.Write(
			"""{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[{"name":"github","status":"connected","source":"user"},{"name":"linear","status":"needs-auth","source":"user"}],"plugins":[{"name":"design","source":"design@market","version":"1.0.0"}]}""",
			"""{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"s1"}""");
		process.Exit();
		await UntilAsync(() => !_chat.Running);

		var tools = await _chat.ToolsAsync(check: false);
		Assert.Equal(["linear", "github"], tools.Servers.Select(s => s.Name));
		Assert.Equal("design@market", Assert.Single(tools.Plugins).Id);
		Assert.EndsWith("(from a message)", ClaudeToolsView.LastChecked(tools, DateTimeOffset.UtcNow));
	}

	[Fact]
	public async Task Tools_CheckNow_RunsTheProbeInTheMirror_FailureKeepsRowsAndSaysWhy()
	{
		await ReadyAsync();
		string? folder = null;
		_server.Probe = f =>
		{
			folder = f;
			return (new ClaudeToolsSnapshot([new("blender", null, "failed", "refused")], [new("design@market", "design", "1.0.0", true)], default, ClaudeToolsSnapshot.FromCheck), null);
		};

		var tools = await _chat.ToolsAsync(check: true);

		Assert.Equal(_server.PathOf(Repo, string.Empty).TrimEnd('\\', '/'), folder!.TrimEnd('\\', '/'));
		Assert.Equal(("blender", "failed", "refused"), (tools.Servers[0].Name, tools.Servers[0].Status, tools.Servers[0].Reason));
		Assert.Equal(ClaudeToolsSnapshot.FromCheck, tools.From);

		_server.Probe = _ => (null, "Check timed out after 60 s.");
		var failed = await _chat.ToolsAsync(check: true);

		Assert.Equal("Check timed out after 60 s.", failed.Error);
		Assert.Equal("blender", Assert.Single(failed.Servers).Name);
	}

	[Fact]
	public async Task Tools_AfterSave_ServerSwitchedOffShowsOff()
	{
		await ReadyAsync();
		_server.Probe = _ => (new ClaudeToolsSnapshot([new("github", null, "connected")], [], default, ClaudeToolsSnapshot.FromCheck), null);
		var row = (await _chat.ToolsAsync(check: true)).Servers[0];
		var form = new ProjectSettingsForm(_sync.Settings);

		ClaudeToolsView.SetOn(form, row, false);
		await _sync.SaveSettingsAsync(form.ToSettings);

		Assert.Equal(["github"], _server.Projects.GetSettings(Repo).AgentDisabledMcpServers);
		var after = (await _chat.ToolsAsync(check: false)).Servers[0];
		Assert.False(after.OnHere);
		Assert.False(ClaudeToolsView.IsOn(new ProjectSettingsForm(_sync.Settings), after));
	}

	private static ClaudeMcpServerRow Server(string name, string status) => new(name, null, status, status != "off");

	private static ClaudeMcpServerRow Pending(string name, string hash) => new(name, "project", ClaudeToolStatuses.Pending, false, Command: "npx -y db-mcp", EntryHash: hash);

	private static ClaudeToolsPayload Payload(DateTimeOffset? at, string? from) => new(Repo, [], [], at, from);

	private static async Task UntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the chat engine.");
			await Task.Delay(10, Ct);
		}
	}

	private async Task ReadyAsync()
	{
		await _sync.InitializeAsync();
		_folder.Write("a.txt", "v1");
		await _sync.OpenFolderAsync();
		await _sync.SyncOnceAsync(Ct);
		await _chat.InitializeAsync();
	}
}
