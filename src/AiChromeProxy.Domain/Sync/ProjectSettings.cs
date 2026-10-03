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

	/// <summary>Keys later sub-projects add (agent command, model, permissions): kept as they are.</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Extra { get; init; }

	/// <summary>Effective value (default true).</summary>
	[JsonIgnore]
	public bool ApplyServerChangesOrDefault => ApplyServerChanges ?? true;
}
