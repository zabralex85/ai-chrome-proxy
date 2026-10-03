namespace AiChromeProxy.Client.Shell;

/// <summary>
/// The centre tabs: the permanent Welcome tab, the Errors tab and one tab per opened file (its id is the file path;
/// the built-in ids start with <c>:</c>, which no synced path can).
/// </summary>
public sealed class TabSet
{
	public const string Welcome = ":welcome";
	public const string Errors = ":errors";

	private readonly List<string> _open = [Welcome];

	/// <summary>Open tabs, in strip order (Welcome first).</summary>
	public IReadOnlyList<string> Open => _open;

	public string Active { get; private set; } = Welcome;

	public static bool IsFile(string id) => !id.StartsWith(':');

	/// <summary>Focuses the tab, opening it at the end of the strip first when needed.</summary>
	public void Show(string id)
	{
		if (!_open.Contains(id))
		{
			_open.Add(id);
		}

		Active = id;
	}

	/// <summary>Closes a tab (Welcome stays); closing the active one activates the tab that takes its place, else the previous one.</summary>
	public void Close(string id)
	{
		var index = _open.IndexOf(id);
		if (id == Welcome || index < 0)
		{
			return;
		}

		_open.RemoveAt(index);
		if (Active == id)
		{
			Active = _open[Math.Min(index, _open.Count - 1)];
		}
	}

	/// <summary>WAI-ARIA tabs with automatic activation: Left/Right move (wrapping), Home/End jump.</summary>
	public void OnKey(string key)
	{
		var index = _open.IndexOf(Active);
		Active = key switch
		{
			"ArrowRight" => _open[(index + 1) % _open.Count],
			"ArrowLeft" => _open[(index - 1 + _open.Count) % _open.Count],
			"Home" => _open[0],
			"End" => _open[^1],
			_ => Active,
		};
	}
}
