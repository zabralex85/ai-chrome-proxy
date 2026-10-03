using AiChromeProxy.Application.Sync;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Application;

/// <summary>In-memory <see cref="IProjectStore"/>: dictionaries per repo, paths ignoring case like the SQLite store.</summary>
public sealed class MemoryProjectStore : IProjectStore
{
	private readonly HashSet<string> _baselined = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Dictionary<string, string>> _bases = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ProjectSettings> _settings = new(StringComparer.Ordinal);

	/// <summary>How many times <see cref="SetBases"/> was called (one transaction each).</summary>
	public int SetBasesCalls { get; private set; }

	public bool IsBaselined(string repo) => _baselined.Contains(repo);

	public void SetBaselined(string repo) => _baselined.Add(repo);

	public IReadOnlyDictionary<string, string> GetBases(string repo) =>
		new Dictionary<string, string>(Bases(repo), StringComparer.OrdinalIgnoreCase);

	public void SetBases(string repo, IReadOnlyCollection<KeyValuePair<string, string?>> changes)
	{
		SetBasesCalls++;
		var bases = Bases(repo);
		foreach (var (path, sha256) in changes)
		{
			if (sha256 is null)
			{
				bases.Remove(path);
			}
			else
			{
				bases[path] = sha256;
			}
		}
	}

	public ProjectSettings GetSettings(string repo) => _settings.GetValueOrDefault(repo) ?? ProjectSettings.Default;

	public ProjectSettings SaveSettings(string repo, ProjectSettings settings) => _settings[repo] = settings;

	private Dictionary<string, string> Bases(string repo)
	{
		if (!_bases.TryGetValue(repo, out var bases))
		{
			_bases[repo] = bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}

		return bases;
	}
}
