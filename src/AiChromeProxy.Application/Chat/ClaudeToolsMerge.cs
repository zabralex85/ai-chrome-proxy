using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Chat;

/// <summary>Decides the rows of the Claude tools section from the last snapshot and the project's switches.</summary>
public static class ClaudeToolsMerge
{
	private const string PluginPrefix = "plugin:";

	/// <summary>
	/// The rows: <see cref="ClaudeToolEntries.ApprovalServer"/> hidden; a server or plugin off in this project has status <c>off</c> when the snapshot
	/// came from a run (Claude did not load it), else its checked status; one that is off here but absent from the snapshot still shows, as
	/// <c>off</c> after a run, <c>missing</c> after a check and <c>unknown</c> without a snapshot. Servers of a plugin carry its name and follow its switch.
	/// Sorted: problems (<c>failed</c>, <c>needs-auth</c>, <c>pending</c>) first, then by name (ordinal, ignoring case).
	/// </summary>
	public static ClaudeToolsPayload Merge(string repo, ClaudeToolsSnapshot? snapshot, ProjectSettings settings, string? error = null)
	{
		var fromRun = snapshot?.From == ClaudeToolsSnapshot.FromRun;
		var absent = snapshot is null ? ClaudeToolStatuses.Unknown : fromRun ? ClaudeToolStatuses.Off : ClaudeToolStatuses.Missing;
		var offServers = ClaudeToolEntries.Clean(settings.AgentDisabledMcpServers).ToHashSet(StringComparer.Ordinal);
		var offPlugins = ClaudeToolEntries.Clean(settings.AgentDisabledPlugins).ToHashSet(StringComparer.Ordinal);
		var offPluginNames = offPlugins.Select(PluginListParser.NameOf).ToHashSet(StringComparer.Ordinal);
		var approved = ClaudeToolEntries.Clean(settings.AgentApprovedMcpServers).ToHashSet(StringComparer.Ordinal);

		var servers = new List<ClaudeMcpServerRow>();
		foreach (var s in snapshot?.Servers ?? [])
		{
			if (s.Name == ClaudeToolEntries.ApprovalServer)
			{
				continue;
			}

			var plugin = PluginOf(s.Name);
			var status = ClaudeToolStatuses.Normalize(s.Status);
			var off = offServers.Contains(s.Name) || (plugin is not null && offPluginNames.Contains(plugin));
			var on = !off && (status != ClaudeToolStatuses.Pending || approved.Contains(s.Name));
			servers.Add(new ClaudeMcpServerRow(s.Name, s.Source, off && fromRun ? ClaudeToolStatuses.Off : status, on, plugin, s.Reason));
		}

		servers.AddRange(offServers
			.Where(n => n != ClaudeToolEntries.ApprovalServer && !servers.Any(r => r.Name == n))
			.Select(n => new ClaudeMcpServerRow(n, null, absent, false, PluginOf(n))));

		var plugins = new List<ClaudePluginRow>();
		foreach (var p in snapshot?.Plugins ?? [])
		{
			var off = offPlugins.Contains(p.Id);
			plugins.Add(new ClaudePluginRow(p.Id, p.Name, p.Version, p.Enabled, !off, off && fromRun ? ClaudeToolStatuses.Off : null));
		}

		plugins.AddRange(offPlugins
			.Where(id => !plugins.Any(r => r.Id == id))
			.Select(id => new ClaudePluginRow(id, PluginListParser.NameOf(id), null, false, false, absent)));

		return new ClaudeToolsPayload(
			repo,
			[.. servers.OrderBy(r => r.Status is ClaudeToolStatuses.Failed or ClaudeToolStatuses.NeedsAuth or ClaudeToolStatuses.Pending ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
			[.. plugins.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)],
			snapshot?.CheckedAt,
			snapshot?.From,
			error);
	}

	/// <summary>The plugin name of a <c>plugin:&lt;plugin&gt;:&lt;server&gt;</c> server, else null.</summary>
	private static string? PluginOf(string name)
	{
		if (!name.StartsWith(PluginPrefix, StringComparison.Ordinal))
		{
			return null;
		}

		var end = name.IndexOf(':', PluginPrefix.Length);
		return end > PluginPrefix.Length ? name[PluginPrefix.Length..end] : null;
	}
}
