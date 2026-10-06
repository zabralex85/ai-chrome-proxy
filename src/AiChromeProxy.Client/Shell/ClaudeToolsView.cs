using System.Globalization;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Client.Shell;

/// <summary>
/// What the <b>Claude tools</b> section of the Project settings tab shows for an <c>agent.tools</c> payload and which of the form's lists its
/// <b>On in this project</b> switches edit: a server's switch edits the disabled servers (for a <c>pending</c> project server: the approved ones), a
/// plugin's the disabled plugins; a plugin's servers have none (the plugin's switch covers them).
/// </summary>
public static class ClaudeToolsView
{
	public const string Empty = "No data yet — send a message or press Check now.";

	public const string SignInHint = "Sign in on the home computer: run `claude`, then `/mcp`.";

	/// <summary>The badge text of a status (<see cref="ClaudeToolStatuses"/>).</summary>
	public static string Badge(string? status) => status switch
	{
		ClaudeToolStatuses.Connected => "Connected",
		ClaudeToolStatuses.NeedsAuth => "Needs sign-in",
		ClaudeToolStatuses.Failed => "Failed",
		ClaudeToolStatuses.NotConfigured => "Not configured",
		ClaudeToolStatuses.Pending => "Waiting for approval",
		ClaudeToolStatuses.Off => "Off in this project",
		ClaudeToolStatuses.Missing => "Not installed",
		_ => "Unknown",
	};

	/// <summary>The badge colour: <c>ok</c>, <c>warn</c>, <c>error</c> or <c>muted</c>.</summary>
	public static string Tone(string? status) => status switch
	{
		ClaudeToolStatuses.Connected => "ok",
		ClaudeToolStatuses.NeedsAuth or ClaudeToolStatuses.Pending => "warn",
		ClaudeToolStatuses.Failed => "error",
		_ => "muted",
	};

	/// <summary>The hint under a problem row (backticks mark code), or null.</summary>
	public static string? Hint(ClaudeMcpServerRow row) => row.Status switch
	{
		ClaudeToolStatuses.NeedsAuth => SignInHint,
		ClaudeToolStatuses.Failed => $"Check the server on the home computer: `claude mcp get {row.Name}`.",
		_ => null,
	};

	/// <summary>A hint split at its backticks: every second part is code.</summary>
	public static IReadOnlyList<(string Text, bool Code)> Parts(string hint) =>
		[.. hint.Split('`').Select((text, i) => (text, i % 2 == 1)).Where(p => p.text.Length > 0)];

	/// <summary>The server's name without the <c>plugin:&lt;plugin&gt;:</c> prefix (its source says the plugin).</summary>
	public static string Name(ClaudeMcpServerRow row) =>
		row.Plugin is { } plugin && $"plugin:{plugin}:" is var prefix && row.Name.StartsWith(prefix, StringComparison.Ordinal) ? row.Name[prefix.Length..] : row.Name;

	/// <summary>The small source text: <c>user</c>, <c>project</c>, <c>plugin X</c>, <c>claude.ai</c>, or empty when unknown.</summary>
	public static string Source(ClaudeMcpServerRow row) => row.Plugin is { } plugin
		? $"plugin {plugin}"
		: row.Source switch
		{
			"claudeai" => "claude.ai",
			"plugin" => "plugin",
			var source => source ?? string.Empty,
		};

	/// <summary>The small text of a plugin: its id, version and whether Claude Code itself has it off.</summary>
	public static string Source(ClaudePluginRow row) =>
		string.Join(" · ", new[] { row.Id, row.Version is { Length: > 0 } version ? "v" + version : null, row.Enabled ? null : "off in Claude Code" }.OfType<string>());

	/// <summary>"Last checked {time} (from a message | by Check now)", or null when nothing was recorded yet.</summary>
	public static string? LastChecked(ClaudeToolsPayload tools, DateTimeOffset now)
	{
		if (tools.CheckedAt is not { } at)
		{
			return null;
		}

		var time = now - at < TimeSpan.FromHours(12) ? Format.Ago(at, now) : at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
		return $"Last checked {time} ({(tools.From == ClaudeToolsSnapshot.FromCheck ? "by Check now" : "from a message")})";
	}

	/// <summary>Whether the row has its own switch (a plugin's server has not).</summary>
	public static bool HasSwitch(ClaudeMcpServerRow row) => row.Plugin is null;

	/// <summary>The switch as the form has it now.</summary>
	public static bool IsOn(ProjectSettingsForm form, ClaudeMcpServerRow row) =>
		!form.DisabledMcpServers.Contains(row.Name) && (row.Status != ClaudeToolStatuses.Pending || form.ApprovedMcpServers.Contains(row.Name));

	/// <summary>Turns a server on or off in the form: off disables it (a pending one is just not approved), on enables (and approves a pending one).</summary>
	public static void SetOn(ProjectSettingsForm form, ClaudeMcpServerRow row, bool on)
	{
		var pending = row.Status == ClaudeToolStatuses.Pending;
		if (on)
		{
			form.DisabledMcpServers.Remove(row.Name);
			if (pending)
			{
				form.ApprovedMcpServers.Add(row.Name);
			}
		}
		else if (pending)
		{
			form.ApprovedMcpServers.Remove(row.Name);
		}
		else
		{
			form.DisabledMcpServers.Add(row.Name);
		}
	}

	public static bool IsOn(ProjectSettingsForm form, ClaudePluginRow row) => !form.DisabledPlugins.Contains(row.Id);

	public static void SetOn(ProjectSettingsForm form, ClaudePluginRow row, bool on)
	{
		if (on)
		{
			form.DisabledPlugins.Remove(row.Id);
		}
		else
		{
			form.DisabledPlugins.Add(row.Id);
		}
	}

	/// <summary>The text for a request that failed.</summary>
	public static string Error(Exception ex) => ex switch
	{
		RequestFailedException or InvalidOperationException => ex.Message,
		TimeoutException => "No answer from the server; try again.",
		_ => "Could not reach the server.",
	};
}
