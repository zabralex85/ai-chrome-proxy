using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>
/// Sync sessions by connection id; the hub closes a connection's session when it disconnects. One <see cref="IMirrorWatcher"/> watch per repo
/// is shared by the sessions that have it open (started by the first, stopped when the last closes or moves to another repo); its changes
/// go to each of those sessions.
/// </summary>
public sealed class SyncSessions(IMirrorStore store, IProjectStore projects, ILogger<SyncSession> logger, TimeProvider time, IMirrorWatcher watcher)
{
	private readonly ConcurrentDictionary<string, SyncSession> _sessions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, (IDisposable? Watch, int Count)> _watches = new(StringComparer.OrdinalIgnoreCase);

	public SyncSession Get(string connectionId) => _sessions.GetOrAdd(connectionId, _ => new SyncSession(store, projects, logger, time, RepoChanged));

	/// <summary>Drops the session and its unfinished upload (temporary file deleted).</summary>
	public void Close(string connectionId)
	{
		if (_sessions.TryRemove(connectionId, out var session))
		{
			session.Dispose();
		}
	}

	/// <summary>A session opened <paramref name="to"/> after <paramref name="from"/>, or was disposed (<paramref name="to"/> null).</summary>
	private void RepoChanged(SyncSession session, string? from, string? to)
	{
		lock (_watches)
		{
			if (from is not null && _watches.TryGetValue(from, out var old))
			{
				if (old.Count > 1)
				{
					_watches[from] = (old.Watch, old.Count - 1);
				}
				else
				{
					_watches.Remove(from);
					old.Watch?.Dispose();
				}
			}

			if (to is null)
			{
				return;
			}

			if (_watches.TryGetValue(to, out var current))
			{
				_watches[to] = (current.Watch, current.Count + 1);
				return;
			}

			IDisposable? watch = null;
			try
			{
				watch = watcher.Watch(to, paths => ChangedAsync(to, paths));
			}
			catch (Exception ex)
			{
				// The session still works; it just does not see server-side edits.
				// ponytail: a failed watch is cached (not retried) until every session has left the repo; retry on the next open if that matters.
				logger.LogError(ex, "Sync {Repo}: cannot watch the mirror folder", to);
			}

			_watches[to] = (watch, 1);
		}
	}

	private async Task ChangedAsync(string repo, IReadOnlyCollection<string>? paths)
	{
		foreach (var session in _sessions.Values.Where(s => string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase)))
		{
			try
			{
				await session.MirrorChangedAsync(paths, CancellationToken.None);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Sync {Repo}: pushing mirror changes failed", repo);
			}
		}
	}
}
