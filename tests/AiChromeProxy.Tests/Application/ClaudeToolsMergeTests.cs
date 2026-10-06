using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Application;

public sealed class ClaudeToolsMergeTests
{
	private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

	private static readonly ClaudeMcpServer[] Servers =
	[
		new("codegraph", "user", ClaudeToolStatuses.Connected),
		new("aicp", "dynamic", ClaudeToolStatuses.Connected),
		new("Blender", "user", ClaudeToolStatuses.Failed, "ECONNREFUSED"),
		new("plugin:design:slack", "plugin", ClaudeToolStatuses.NeedsAuth),
		new("team-db", "project", ClaudeToolStatuses.Pending),
		new("other-db", "project", ClaudeToolStatuses.Pending),
		new("weird", "user", "disabled"),
		new("notion", "claudeai", ClaudeToolStatuses.NotConfigured),
	];

	private static readonly ClaudePlugin[] Plugins =
	[
		new("design@market", "design", "1.0.0", true),
		new("caveman@caveman", "caveman", null, false),
	];

	[Fact]
	public void NoSnapshot_NoSwitches_Empty()
	{
		var payload = ClaudeToolsMerge.Merge("repo", null, ProjectSettings.Default);

		Assert.Equal(("repo", null, null, null), (payload.Repo, payload.CheckedAt, payload.From, payload.Error));
		Assert.Empty(payload.Servers);
		Assert.Empty(payload.Plugins);
	}

	[Fact]
	public void NoSnapshot_DisabledEntries_Unknown()
	{
		var payload = ClaudeToolsMerge.Merge("repo", null, new ProjectSettings { AgentDisabledMcpServers = ["x", "aicp"], AgentDisabledPlugins = ["p@m"] });

		Assert.Equal([new ClaudeMcpServerRow("x", null, ClaudeToolStatuses.Unknown, false)], payload.Servers);
		Assert.Equal([new ClaudePluginRow("p@m", "p", null, false, false, ClaudeToolStatuses.Unknown)], payload.Plugins);
	}

	[Fact]
	public void Check_StatusesKept_AicpHidden_PendingNeedsApproval_ProblemsFirstThenName()
	{
		var payload = ClaudeToolsMerge.Merge("repo", Snapshot(ClaudeToolsSnapshot.FromCheck), new ProjectSettings { AgentApprovedMcpServers = ["team-db"] }, "boom");

		Assert.Equal((At, "check", "boom"), (payload.CheckedAt!.Value, payload.From!, payload.Error!));
		Assert.Equal(
			[
				new ClaudeMcpServerRow("Blender", "user", ClaudeToolStatuses.Failed, true, null, "ECONNREFUSED"),
				new ClaudeMcpServerRow("other-db", "project", ClaudeToolStatuses.Pending, false),
				new ClaudeMcpServerRow("plugin:design:slack", "plugin", ClaudeToolStatuses.NeedsAuth, true, "design"),
				new ClaudeMcpServerRow("team-db", "project", ClaudeToolStatuses.Pending, true),
				new ClaudeMcpServerRow("codegraph", "user", ClaudeToolStatuses.Connected, true),
				new ClaudeMcpServerRow("notion", "claudeai", ClaudeToolStatuses.NotConfigured, true),
				new ClaudeMcpServerRow("weird", "user", ClaudeToolStatuses.Unknown, true),
			],
			payload.Servers);
		Assert.Equal(
			[
				new ClaudePluginRow("caveman@caveman", "caveman", null, false, true),
				new ClaudePluginRow("design@market", "design", "1.0.0", true, true),
			],
			payload.Plugins);
	}

	[Theory]
	[InlineData(ClaudeToolsSnapshot.FromRun, ClaudeToolStatuses.Off, ClaudeToolStatuses.Off, ClaudeToolStatuses.Off, ClaudeToolStatuses.Off, ClaudeToolStatuses.Off)]
	[InlineData(ClaudeToolsSnapshot.FromCheck, ClaudeToolStatuses.Failed, ClaudeToolStatuses.NeedsAuth, ClaudeToolStatuses.Missing, null, ClaudeToolStatuses.Missing)]
	public void OffHere_FromRunIsOff_FromCheckKeepsStatus_AbsentIsMissing(string from, string blender, string slack, string gone, string? design, string old)
	{
		var settings = new ProjectSettings
		{
			AgentDisabledMcpServers = ["Blender", " gone ", "aicp", "bad\u0001"],
			AgentDisabledPlugins = ["design@market", "old@market"],
		};

		var payload = ClaudeToolsMerge.Merge("repo", Snapshot(from), settings);

		var rows = payload.Servers.ToDictionary(r => r.Name);
		Assert.Equal(new ClaudeMcpServerRow("Blender", "user", blender, false, null, "ECONNREFUSED"), rows["Blender"]);
		Assert.Equal(new ClaudeMcpServerRow("plugin:design:slack", "plugin", slack, false, "design"), rows["plugin:design:slack"]);
		Assert.Equal(new ClaudeMcpServerRow("gone", null, gone, false), rows["gone"]);
		Assert.True(rows["codegraph"].OnHere);
		Assert.Equal(["Blender", "codegraph", "gone", "notion", "other-db", "plugin:design:slack", "team-db", "weird"], rows.Keys.Order(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			[
				new ClaudePluginRow("caveman@caveman", "caveman", null, false, true),
				new ClaudePluginRow("design@market", "design", "1.0.0", true, false, design),
				new ClaudePluginRow("old@market", "old", null, false, false, old),
			],
			payload.Plugins);
	}

	[Fact]
	public void PluginName_OnlyFromPluginPrefixWithServerPart()
	{
		var snapshot = new ClaudeToolsSnapshot([new("plugin:", null, "connected"), new("plugin:x", null, "connected"), new("plugin::y", null, "connected")], [], At, ClaudeToolsSnapshot.FromCheck);

		Assert.All(ClaudeToolsMerge.Merge("repo", snapshot, ProjectSettings.Default).Servers, r => Assert.Null(r.Plugin));
	}

	private static ClaudeToolsSnapshot Snapshot(string from) => new(Servers, Plugins, At, from);
}
