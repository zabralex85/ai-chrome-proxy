using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>Sync sessions by connection id; the hub closes a connection's session when it disconnects.</summary>
public sealed class SyncSessions(IMirrorStore store, ILogger<SyncSession> logger, TimeProvider time)
{
	private readonly ConcurrentDictionary<string, SyncSession> _sessions = new(StringComparer.Ordinal);

	public SyncSession Get(string connectionId) => _sessions.GetOrAdd(connectionId, _ => new SyncSession(store, logger, time));

	/// <summary>Drops the session and its unfinished upload (temporary file deleted).</summary>
	public void Close(string connectionId)
	{
		if (_sessions.TryRemove(connectionId, out var session))
		{
			session.Dispose();
		}
	}
}
