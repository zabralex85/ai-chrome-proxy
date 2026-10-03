using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Infrastructure.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>SQLite implementation of <see cref="IChatStore"/> in the same <c>aicp.db</c> as the project store: one connection per call.</summary>
public sealed class SqliteChatStore : IChatStore
{
	private readonly string _path;
	private readonly TimeProvider _time;

	public SqliteChatStore(IOptions<ProjectsOptions> options, TimeProvider time)
	{
		_path = options.Value.Database;
		_time = time;
	}

	public ChatSessionInfo CreateSession(string repo, string title)
	{
		var id = Guid.NewGuid().ToString("N");
		var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var command = Command(connection, "INSERT INTO chat_session (id, repo, title, created, updated) VALUES ($id, $repo, $title, $now, $now)", ("$id", id), ("$repo", repo), ("$title", title), ("$now", now)))
			{
				command.ExecuteNonQuery();
			}
		}

		return new ChatSessionInfo(id, title, DateTimeOffset.FromUnixTimeMilliseconds(now), false);
	}

	public IReadOnlyList<ChatSessionInfo> ListSessions(string repo)
	{
		var sessions = new List<ChatSessionInfo>();
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var command = Command(connection, "SELECT id, title, updated FROM chat_session WHERE repo = $repo ORDER BY updated DESC, rowid DESC", ("$repo", repo)))
			{
				using (var reader = command.ExecuteReader())
				{
					while (reader.Read())
					{
						sessions.Add(new ChatSessionInfo(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), false));
					}
				}
			}
		}

		return sessions;
	}

	public void SetClaudeSession(string id, string claudeId)
	{
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var command = Command(connection, "UPDATE chat_session SET claude_session_id = $claude WHERE id = $id", ("$id", id), ("$claude", claudeId)))
			{
				RequireRow(command.ExecuteNonQuery(), id);
			}
		}
	}

	public string? GetClaudeSession(string id) => Scalar("SELECT claude_session_id FROM chat_session WHERE id = $id", ("$id", id)) as string;

	public void Touch(string id)
	{
		using (var connection = AicpDatabase.Open(_path))
		{
			Touch(connection, null, id);
		}
	}

	public IReadOnlyList<ChatEvent> Append(string sessionId, IReadOnlyList<ChatEvent> events)
	{
		if (events.Any(e => e.Kind == ChatEventKinds.Text))
		{
			throw new ArgumentException("Streaming text deltas are not stored.", nameof(events));
		}

		var stored = new List<ChatEvent>(events.Count);
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var transaction = connection.BeginTransaction())
			{
				Touch(connection, transaction, sessionId); // throws for an unknown session before anything is inserted
				foreach (var e in events)
				{
					// One statement picks the number and inserts, so concurrent writers cannot take the same seq.
					var json = JsonSerializer.Serialize(e with { SessionId = sessionId, Seq = 0 }, JsonSerializerOptions.Web);
					using (var insert = Command(connection, "INSERT INTO chat_event (session_id, seq, json) SELECT $id, COALESCE(MAX(seq), 0) + 1, $json FROM chat_event WHERE session_id = $id RETURNING seq", ("$id", sessionId), ("$json", json)))
					{
						insert.Transaction = transaction;
						stored.Add(e with { SessionId = sessionId, Seq = Convert.ToInt64(insert.ExecuteScalar()) });
					}
				}

				transaction.Commit();
			}
		}

		return stored;
	}

	public (IReadOnlyList<ChatEvent> Events, bool Final) Read(string sessionId, long afterSeq, int maxBytes)
	{
		var events = new List<ChatEvent>();
		var total = 0;
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var command = Command(connection, "SELECT seq, json FROM chat_event WHERE session_id = $id AND seq > $after ORDER BY seq", ("$id", sessionId), ("$after", afterSeq)))
			{
				using (var reader = command.ExecuteReader())
				{
					while (reader.Read())
					{
						var e = JsonSerializer.Deserialize<ChatEvent>(reader.GetString(1), JsonSerializerOptions.Web)! with { Seq = reader.GetInt64(0) };
						var size = JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length;
						if (events.Count > 0 && total + size > maxBytes)
						{
							return (events, false);
						}

						events.Add(e);
						total += size;
					}
				}
			}
		}

		return (events, true);
	}

	public string? SessionRepo(string id) => Scalar("SELECT repo FROM chat_session WHERE id = $id", ("$id", id)) as string;

	private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
	{
		var command = connection.CreateCommand();
		command.CommandText = sql;
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}

		return command;
	}

	private static void RequireRow(int affected, string id)
	{
		if (affected == 0)
		{
			throw new KeyNotFoundException($"Unknown chat session '{id}'.");
		}
	}

	private void Touch(SqliteConnection connection, SqliteTransaction? transaction, string id)
	{
		using (var command = Command(connection, "UPDATE chat_session SET updated = $now WHERE id = $id", ("$id", id), ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds())))
		{
			command.Transaction = transaction;
			RequireRow(command.ExecuteNonQuery(), id);
		}
	}

	private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
	{
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var command = Command(connection, sql, parameters))
			{
				var value = command.ExecuteScalar();
				return value is DBNull ? null : value;
			}
		}
	}
}
