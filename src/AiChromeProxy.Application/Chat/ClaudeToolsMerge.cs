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
	/// <c>off</c> after a run, <c>missing</c> after a check and <c>unknown</c> without a snapshot (with no switch of a plugin). Servers of a plugin carry
	/// its name and follow its switch. A server of the repo's <c>.mcp.json</c> (<paramref name="project"/>; not a plugin's or claude.ai's) carries its
	/// command and entry hash; it is approved only by an approval of that hash, and one approved before its entry changed is <c>pending</c> again.
	/// A name or id reported twice shows once. Sorted: problems (<c>failed</c>, <c>needs-auth</c>, <c>pending</c>) first, then by name (ordinal, ignoring case).
	/// </summary>
	public static ClaudeToolsPayload Merge(
		string repo,
		ClaudeToolsSnapshot? snapshot,
		ProjectSettings settings,
		string? error = null,
		IReadOnlyDictionary<string, McpJsonEntry>? project = null)
	{
		var fromRun = snapshot?.From == ClaudeToolsSnapshot.FromRun;
		var absent = snapshot is null ? ClaudeToolStatuses.Unknown : fromRun ? ClaudeToolStatuses.Off : ClaudeToolStatuses.Missing;
		var offServers = ClaudeToolEntries.Clean(settings.AgentDisabledMcpServers).ToHashSet(StringComparer.Ordinal);
		var offPlugins = ClaudeToolEntries.Clean(settings.AgentDisabledPlugins).ToHashSet(StringComparer.Ordinal);
		var offPluginNames = offPlugins.Select(PluginListParser.NameOf).ToHashSet(StringComparer.Ordinal);
		var approvals = ClaudeToolEntries.Clean(settings.AgentApprovedMcpServers, ClaudeToolEntries.MaxApprovalLength);
		var approved = approvals.ToHashSet(StringComparer.Ordinal);
		var approvedNames = approvals.Select(ClaudeToolEntries.ApprovalName).ToHashSet(StringComparer.Ordinal);

		var servers = new List<ClaudeMcpServerRow>();
		foreach (var s in (snapshot?.Servers ?? []).DistinctBy(s => s.Name, StringComparer.Ordinal))
		{
			if (s.Name == ClaudeToolEntries.ApprovalServer)
			{
				continue;
			}

			var plugin = PluginOf(s.Name);
			var entry = (s.Source is null or "project") && plugin is null ? project?.GetValueOrDefault(s.Name) : null;
			var isApproved = entry is not null && approved.Contains(ClaudeToolEntries.Approval(s.Name, entry.Hash));
			var status = entry is not null && !isApproved && approvedNames.Contains(s.Name) ? ClaudeToolStatuses.Pending : ClaudeToolStatuses.Normalize(s.Status);
			var off = offServers.Contains(s.Name) || (plugin is not null && offPluginNames.Contains(plugin));
			var on = !off && (status != ClaudeToolStatuses.Pending || isApproved);
			servers.Add(new ClaudeMcpServerRow(s.Name, s.Source, off && fromRun ? ClaudeToolStatuses.Off : status, on, plugin, s.Reason, entry?.Command, entry?.Hash));
		}

		servers.AddRange(offServers
			.Where(n => n != ClaudeToolEntries.ApprovalServer && !servers.Any(r => r.Name == n))
			.Select(n => new ClaudeMcpServerRow(n, null, absent, false)));

		var plugins = new List<ClaudePluginRow>();
		foreach (var p in (snapshot?.Plugins ?? []).DistinctBy(p => p.Id, StringComparer.Ordinal))
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
