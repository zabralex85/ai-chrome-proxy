using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Sync;

/// <summary>Per-repo state that must survive restarts: the agreed hash per file (the base), whether the repo is baselined, and its settings.</summary>
public interface IProjectStore
{
	bool IsBaselined(string repo);

	void SetBaselined(string repo);

	/// <summary>The base of each path (case-insensitive) that has one.</summary>
	IReadOnlyDictionary<string, string> GetBases(string repo);

	/// <summary>Sets (sha256 not null) or removes (null) bases in one transaction.</summary>
	void SetBases(string repo, IReadOnlyCollection<KeyValuePair<string, string?>> changes);

	/// <summary>Forgets the repo's bases and its baseline in one transaction (its settings stay): the next full manifest starts over.</summary>
	void Reset(string repo);

	ProjectSettings GetSettings(string repo);

	/// <summary>Replaces the settings (null properties remove the key; Extra keys are written as they are).</summary>
	ProjectSettings SaveSettings(string repo, ProjectSettings settings);
}
