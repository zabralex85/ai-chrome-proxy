using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Shell;

/// <summary>The editable state of the Project settings tab against the settings last saved (unknown keys are kept as they are).</summary>
public sealed class ProjectSettingsForm(ProjectSettings saved)
{
	/// <summary>The extra excludes, one pattern per line.</summary>
	public string Excludes { get; set; } = saved.Excludes ?? string.Empty;

	public bool Apply { get; set; } = saved.ApplyServerChangesOrDefault;

	/// <summary>How Claude may act: <c>ask</c>, <c>all</c> or <c>settings</c> (<see cref="ProjectSettings.AgentPermissions"/>).</summary>
	public string Permissions { get; set; } = saved.AgentPermissionsOrDefault;

	/// <summary>The Claude model; empty = Claude Code's default.</summary>
	public string Model { get; set; } = saved.AgentModel ?? string.Empty;

	public bool IsDirty =>
		Excludes != (saved.Excludes ?? string.Empty)
		|| Apply != saved.ApplyServerChangesOrDefault
		|| Permissions != saved.AgentPermissionsOrDefault
		|| Model != (saved.AgentModel ?? string.Empty);

	public ProjectSettings ToSettings() => saved with
	{
		Excludes = string.IsNullOrWhiteSpace(Excludes) ? null : Excludes,
		ApplyServerChanges = Apply,
		AgentPermissions = Permissions == "ask" ? null : Permissions,
		AgentModel = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
	};
}
