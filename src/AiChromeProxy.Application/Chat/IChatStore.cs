using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>Chat sessions and their stored events (everything except streaming <c>text</c> deltas), kept across restarts.</summary>
public interface IChatStore
{
	/// <summary>Creates a session in the repo; <c>Updated</c> is now, <c>Running</c> false.</summary>
	ChatSessionInfo CreateSession(string repo, string title);

	/// <summary>The repo's sessions, most recently updated first; <c>Running</c> is always false (the service knows).</summary>
	IReadOnlyList<ChatSessionInfo> ListSessions(string repo);

	/// <summary>Remembers Claude's own session id, used with <c>--resume</c> from the next run.</summary>
	void SetClaudeSession(string id, string claudeId);

	/// <summary>Claude's session id, or null when none was recorded (or the session is unknown).</summary>
	string? GetClaudeSession(string id);

	/// <summary>Sets <c>Updated</c> to now.</summary>
	void Touch(string id);

	/// <summary>
	/// Stores the events in order with <c>Seq</c> = last + 1 each (atomic per session) and touches the session; returns them as stored.
	/// Throws <see cref="ArgumentException"/> for a <c>text</c> event: deltas are never stored.
	/// </summary>
	IReadOnlyList<ChatEvent> Append(string sessionId, IReadOnlyList<ChatEvent> events);

	/// <summary>
	/// Events with <c>Seq</c> greater than <paramref name="afterSeq"/>, in order, whose serialized sizes sum to at most
	/// <paramref name="maxBytes"/> (one event at least); <c>Final</c> is true when none follow.
	/// </summary>
	(IReadOnlyList<ChatEvent> Events, bool Final) Read(string sessionId, long afterSeq, int maxBytes);

	/// <summary>The repo a session belongs to, or null when it does not exist.</summary>
	string? SessionRepo(string id);
}
