using AiChromeProxy.Application.Sync;

namespace AiChromeProxy.Tests.Application;

/// <summary>Records <see cref="Watch"/> calls and disposals; a test raises changes by hand.</summary>
public sealed class FakeMirrorWatcher : IMirrorWatcher
{
	private readonly List<(string Repo, Func<IReadOnlyCollection<string>?, Task> Changed, Handle Handle)> _watches = [];

	/// <summary>Every repo watched so far, in order.</summary>
	public IReadOnlyList<string> Started => [.. _watches.Select(w => w.Repo)];

	/// <summary>Repos whose watch is not disposed.</summary>
	public IReadOnlyList<string> Active => [.. _watches.Where(w => !w.Handle.Disposed).Select(w => w.Repo)];

	/// <summary>Thrown by <see cref="Watch"/> when set.</summary>
	public Exception? Throws { get; set; }

	public IDisposable Watch(string repo, Func<IReadOnlyCollection<string>?, Task> changed)
	{
		if (Throws is not null)
		{
			throw Throws;
		}

		var handle = new Handle();
		_watches.Add((repo, changed, handle));
		return handle;
	}

	/// <summary>Calls the callbacks of the active watches of <paramref name="repo"/>.</summary>
	public async Task RaiseAsync(string repo, IReadOnlyCollection<string>? paths)
	{
		foreach (var watch in _watches.Where(w => w.Repo == repo && !w.Handle.Disposed).ToList())
		{
			await watch.Changed(paths);
		}
	}

	private sealed class Handle : IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}
}
