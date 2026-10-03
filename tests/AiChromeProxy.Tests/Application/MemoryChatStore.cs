using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Tests.Application;

/// <summary>In-memory <see cref="IChatStore"/> behaving like the SQLite one; time comes from a <see cref="TimeProvider"/>.</summary>
public sealed class MemoryChatStore : IChatStore
{
	private readonly TimeProvider _time;
	private readonly Lock _lock = new();
	private readonly List<Session> _sessions = [];

	public MemoryChatStore(TimeProvider? time = null)
	{
		_time = time ?? TimeProvider.System;
	}

	public ChatSessionInfo CreateSession(string repo, string title)
	{
		lock (_lock)
		{
			var session = new Session(Guid.NewGuid().ToString("N"), repo, title, _time.GetUtcNow());
			_sessions.Add(session);
			return session.Info();
		}
	}

	public IReadOnlyList<ChatSessionInfo> ListSessions(string repo)
	{
		lock (_lock)
		{
			return Enumerable.Reverse(_sessions).Where(s => s.Repo == repo).OrderByDescending(s => s.Updated).Select(s => s.Info()).ToList();
		}
	}

	public void SetClaudeSession(string id, string claudeId)
	{
		lock (_lock)
		{
			Find(id).ClaudeId = claudeId;
		}
	}

	public string? GetClaudeSession(string id)
	{
		lock (_lock)
		{
			return _sessions.FirstOrDefault(s => s.Id == id)?.ClaudeId;
		}
	}

	public void Touch(string id)
	{
		lock (_lock)
		{
			Find(id).Updated = _time.GetUtcNow();
		}
	}

	public IReadOnlyList<ChatEvent> Append(string sessionId, IReadOnlyList<ChatEvent> events)
	{
		if (events.Any(e => e.Kind == ChatEventKinds.Text))
		{
			throw new ArgumentException("Streaming text deltas are not stored.", nameof(events));
		}

		lock (_lock)
		{
			var session = Find(sessionId);
			var stored = new List<ChatEvent>();
			foreach (var e in events)
			{
				var item = e with { SessionId = sessionId, Seq = session.Events.Count + 1 };
				session.Events.Add(item);
				stored.Add(item);
			}

			session.Updated = _time.GetUtcNow();
			return stored;
		}
	}

	public (IReadOnlyList<ChatEvent> Events, bool Final) Read(string sessionId, long afterSeq, int maxBytes)
	{
		lock (_lock)
		{
			var page = new List<ChatEvent>();
			var total = 0;
			foreach (var e in Find(sessionId).Events.Where(e => e.Seq > afterSeq))
			{
				var size = JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length;
				if (page.Count > 0 && total + size > maxBytes)
				{
					return (page, false);
				}

				page.Add(e);
				total += size;
			}

			return (page, true);
		}
	}

	public string? SessionRepo(string id)
	{
		lock (_lock)
		{
			return _sessions.FirstOrDefault(s => s.Id == id)?.Repo;
		}
	}

	private Session Find(string id) => _sessions.First(s => s.Id == id);

	private sealed class Session(string id, string repo, string title, DateTimeOffset updated)
	{
		public string Id { get; } = id;

		public string Repo { get; } = repo;

		public string Title { get; } = title;

		public DateTimeOffset Updated { get; set; } = updated;

		public string? ClaudeId { get; set; }

		public List<ChatEvent> Events { get; } = [];

		public ChatSessionInfo Info() => new(Id, Title, Updated, false);
	}
}
