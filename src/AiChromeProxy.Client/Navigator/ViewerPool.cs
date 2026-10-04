namespace AiChromeProxy.Client.Navigator;

/// <summary>Which open file tabs hold a live viewer instance: at most <see cref="Capacity"/>, the least recently shown one is given up first.</summary>
/// <param name="capacity">The most live instances.</param>
public sealed class ViewerPool(int capacity = ViewerPool.Capacity)
{
	/// <summary>The most live viewer instances.</summary>
	public const int Capacity = 10;

	/// <summary>Paths, the least recently shown first.</summary>
	private readonly List<string> _live = [];

	/// <summary>Gets the paths that hold a live instance, the least recently shown first.</summary>
	public IReadOnlyList<string> Live => _live;

	/// <summary>Whether <paramref name="path"/> may hold a live instance.</summary>
	/// <param name="path">The file's path.</param>
	/// <returns>True when live.</returns>
	public bool Contains(string path) => _live.Contains(path);

	/// <summary>Marks <paramref name="path"/> as the most recently shown one.</summary>
	/// <param name="path">The file's path.</param>
	/// <returns>The paths whose instances must be disposed to stay within the capacity.</returns>
	public IReadOnlyList<string> Touch(string path)
	{
		_live.Remove(path);
		_live.Add(path);
		var evicted = _live.Take(Math.Max(0, _live.Count - capacity)).ToList();
		_live.RemoveRange(0, evicted.Count);
		return evicted;
	}

	/// <summary>Forgets the paths that are not in <paramref name="open"/> (their tabs closed).</summary>
	/// <param name="open">The paths of the open file tabs.</param>
	public void Keep(IReadOnlyCollection<string> open) => _live.RemoveAll(p => !open.Contains(p));

	/// <summary>Forgets every path (the folder changed).</summary>
	public void Clear() => _live.Clear();
}
