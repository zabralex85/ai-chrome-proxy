using Microsoft.Data.Sqlite;

namespace AiChromeProxy.Infrastructure.Projects;

/// <summary>The one SQLite file (<c>aicp.db</c>) shared by the project and chat stores: opening it and keeping its schema at <see cref="Version"/>.</summary>
internal static class AicpDatabase
{
	/// <summary>1 = repo, base, setting; 2 adds chat_session and chat_event.</summary>
	public const int Version = 2;

	private const string ProjectTables = """
		CREATE TABLE IF NOT EXISTS repo (name TEXT PRIMARY KEY, baselined INTEGER NOT NULL DEFAULT 0);
		CREATE TABLE IF NOT EXISTS base (repo TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, sha256 TEXT NOT NULL, PRIMARY KEY (repo, path));
		CREATE TABLE IF NOT EXISTS setting (repo TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (repo, key));
		""";

	private const string ChatTables = """
		CREATE TABLE IF NOT EXISTS chat_session (id TEXT PRIMARY KEY, repo TEXT NOT NULL, title TEXT NOT NULL, claude_session_id TEXT NULL, created INTEGER NOT NULL, updated INTEGER NOT NULL);
		CREATE INDEX IF NOT EXISTS chat_session_repo ON chat_session (repo, updated DESC);
		CREATE TABLE IF NOT EXISTS chat_event (session_id TEXT NOT NULL, seq INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (session_id, seq));
		""";

	private static readonly Lock InitLock = new();
	private static readonly HashSet<string> Initialized = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Creates or upgrades the schema once per path and process; a new database gets version 2, a version 1 one gains the chat tables.</summary>
	public static void EnsureSchema(string path)
	{
		lock (InitLock)
		{
			if (Initialized.Contains(path))
			{
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			using (var connection = Connect(path))
			{
				connection.Open();
				Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
				using (var read = connection.CreateCommand())
				{
					read.CommandText = "PRAGMA user_version";
					if (Convert.ToInt64(read.ExecuteScalar()) < Version)
					{
						using (var transaction = connection.BeginTransaction())
						{
							Execute(connection, transaction, ProjectTables);
							Execute(connection, transaction, ChatTables);
							Execute(connection, transaction, $"PRAGMA user_version={Version}");
							transaction.Commit();
						}
					}
				}
			}

			Initialized.Add(path);
		}
	}

	/// <summary>An open connection to the database (schema ensured).</summary>
	public static SqliteConnection Open(string path)
	{
		EnsureSchema(path);
		var connection = Connect(path);
		connection.Open();
		return connection;
	}

	private static SqliteConnection Connect(string path) => new($"Data Source={path};Pooling=True");

	private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
	{
		using (var command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = sql;
			command.ExecuteNonQuery();
		}
	}
}
