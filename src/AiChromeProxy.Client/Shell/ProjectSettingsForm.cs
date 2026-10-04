using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Shell;

/// <summary>
/// The editable state of the Project settings tab against the settings it was loaded with. Saving (<see cref="ToSettings(ProjectSettings)"/>)
/// replaces only the fields edited here, so that what changed on the server meanwhile (an <b>Allow always</b> rule) is kept; unknown keys
/// are kept as they are.
/// </summary>
public sealed class ProjectSettingsForm(ProjectSettings saved)
{
	/// <summary>The extra excludes, one pattern per line.</summary>
	public string Excludes { get; set; } = SavedExcludes(saved);

	public bool Apply { get; set; } = saved.ApplyServerChangesOrDefault;

	/// <summary>How Claude may act: <c>ask</c>, <c>all</c> or <c>settings</c> (<see cref="ProjectSettings.AgentPermissions"/>).</summary>
	public string Permissions { get; set; } = saved.AgentPermissionsOrDefault;

	/// <summary>The Claude model; empty = Claude Code's default.</summary>
	public string Model { get; set; } = SavedModel(saved);

	/// <summary>The always-allowed rules (<see cref="ProjectSettings.AgentAllowedTools"/>), one per line.</summary>
	public string AllowedTools { get; set; } = SavedAllowedTools(saved);

	public bool IsDirty =>
		Excludes != SavedExcludes(saved)
		|| Apply != saved.ApplyServerChangesOrDefault
		|| Permissions != saved.AgentPermissionsOrDefault
		|| Model != SavedModel(saved)
		|| AllowedTools != SavedAllowedTools(saved);

	/// <summary>The settings as loaded, with the edits.</summary>
	public ProjectSettings ToSettings() => ToSettings(saved);

	/// <summary>The current settings (read again right before saving) with just the fields edited here replaced.</summary>
	/// <param name="current">The server's settings now.</param>
	public ProjectSettings ToSettings(ProjectSettings current)
	{
		var settings = current;
		if (Excludes != SavedExcludes(saved))
		{
			settings = settings with { Excludes = string.IsNullOrWhiteSpace(Excludes) ? null : Excludes };
		}

		if (Apply != saved.ApplyServerChangesOrDefault)
		{
			settings = settings with { ApplyServerChanges = Apply };
		}

		if (Permissions != saved.AgentPermissionsOrDefault)
		{
			settings = settings with { AgentPermissions = Permissions == "ask" ? null : Permissions };
		}

		if (Model != SavedModel(saved))
		{
			settings = settings with { AgentModel = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim() };
		}

		if (AllowedTools != SavedAllowedTools(saved))
		{
			string[] rules = [.. AllowedTools.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];
			settings = settings with { AgentAllowedTools = rules.Length == 0 ? null : rules };
		}

		return settings;
	}

	private static string SavedExcludes(ProjectSettings s) => s.Excludes ?? string.Empty;

	private static string SavedModel(ProjectSettings s) => s.AgentModel ?? string.Empty;

	private static string SavedAllowedTools(ProjectSettings s) => string.Join('\n', s.AgentAllowedTools ?? []);
}
