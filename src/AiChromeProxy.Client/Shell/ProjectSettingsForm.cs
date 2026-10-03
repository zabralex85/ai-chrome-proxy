using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Shell;

/// <summary>The editable state of the Project settings tab against the settings last saved (unknown keys are kept as they are).</summary>
public sealed class ProjectSettingsForm(ProjectSettings saved)
{
	/// <summary>The extra excludes, one pattern per line.</summary>
	public string Excludes { get; set; } = saved.Excludes ?? string.Empty;

	public bool Apply { get; set; } = saved.ApplyServerChangesOrDefault;

	public bool IsDirty => Excludes != (saved.Excludes ?? string.Empty) || Apply != saved.ApplyServerChangesOrDefault;

	public ProjectSettings ToSettings() => saved with
	{
		Excludes = string.IsNullOrWhiteSpace(Excludes) ? null : Excludes,
		ApplyServerChanges = Apply,
	};
}
