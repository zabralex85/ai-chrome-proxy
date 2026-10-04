using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiChromeProxy.Domain.Sync;

/// <summary>Per-project settings; unknown keys are kept as they are.</summary>
public sealed record ProjectSettings
{
	public static readonly ProjectSettings Default = new();

	/// <summary>Extra excludes in <c>.gitignore</c> syntax (see <see cref="IgnoreRules.Create"/>).</summary>
	public string? Excludes { get; init; }

	/// <summary>Whether server changes go back to the browser folder (null = default, true).</summary>
	public bool? ApplyServerChanges { get; init; }

	/// <summary>How Claude may act: <c>ask</c> (default), <c>all</c> or <c>settings</c>; anything else counts as <c>ask</c>.</summary>
	public string? AgentPermissions { get; init; }

	/// <summary>Claude model; null or empty = Claude Code's default.</summary>
	public string? AgentModel { get; init; }

	/// <summary>Tool rules passed as <c>--allowedTools</c> (e.g. <c>Bash(ls)</c>); "Allow always" adds to it.</summary>
	public IReadOnlyList<string>? AgentAllowedTools { get; init; }

	/// <summary>Keys later sub-projects add: kept as they are.</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Extra { get; init; }

	/// <summary>Effective value (default true).</summary>
	[JsonIgnore]
	public bool ApplyServerChangesOrDefault => ApplyServerChanges ?? true;

	/// <summary>Effective value: <c>ask</c>, <c>all</c> or <c>settings</c> (default <c>ask</c>).</summary>
	[JsonIgnore]
	public string AgentPermissionsOrDefault => AgentPermissions is "all" or "settings" ? AgentPermissions : "ask";
}
