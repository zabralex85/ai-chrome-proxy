using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Domain.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Projects;

/// <summary>SQLite (WAL) implementation of <see cref="IProjectStore"/>: one connection per call, schema created once per instance.</summary>
public sealed class SqliteProjectStore : IProjectStore
{
	private const string CreateTables = """
		CREATE TABLE IF NOT EXISTS repo (name TEXT PRIMARY KEY, baselined INTEGER NOT NULL DEFAULT 0);
		CREATE TABLE IF NOT EXISTS base (repo TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, sha256 TEXT NOT NULL, PRIMARY KEY (repo, path));
		CREATE TABLE IF NOT EXISTS setting (repo TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (repo, key));
		""";

	private readonly string _path;
	private readonly Lock _initLock = new();
	private bool _initialized;

	public SqliteProjectStore(IOptions<ProjectsOptions> options)
	{
		_path = options.Value.Database;
	}

	public bool IsBaselined(string repo)
	{
		using (var connection = Open())
		{
			using (var command = Command(connection, "SELECT baselined FROM repo WHERE name = $repo", ("$repo", repo)))
			{
				return command.ExecuteScalar() is long value && value != 0;
			}
		}
	}

	public void SetBaselined(string repo)
	{
		using (var connection = Open())
		{
			using (var command = Command(connection, "INSERT INTO repo (name, baselined) VALUES ($repo, 1) ON CONFLICT(name) DO UPDATE SET baselined = 1", ("$repo", repo)))
			{
				command.ExecuteNonQuery();
			}
		}
	}

	public IReadOnlyDictionary<string, string> GetBases(string repo)
	{
		var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		using (var connection = Open())
		{
			using (var command = Command(connection, "SELECT path, sha256 FROM base WHERE repo = $repo", ("$repo", repo)))
			{
				using (var reader = command.ExecuteReader())
				{
					while (reader.Read())
					{
						bases[reader.GetString(0)] = reader.GetString(1);
					}
				}
			}
		}

		return bases;
	}

	public void SetBases(string repo, IReadOnlyCollection<KeyValuePair<string, string?>> changes)
	{
		using (var connection = Open())
		{
			using (var transaction = connection.BeginTransaction())
			{
				foreach (var (path, sha256) in changes)
				{
					var sql = sha256 is null
						? "DELETE FROM base WHERE repo = $repo AND path = $path"
						: "INSERT INTO base (repo, path, sha256) VALUES ($repo, $path, $sha) ON CONFLICT(repo, path) DO UPDATE SET sha256 = excluded.sha256";
					using (var command = Command(connection, sql, ("$repo", repo), ("$path", path), ("$sha", sha256)))
					{
						command.Transaction = transaction;
						command.ExecuteNonQuery();
					}
				}

				transaction.Commit();
			}
		}
	}

	public void Reset(string repo)
	{
		using (var connection = Open())
		{
			using (var transaction = connection.BeginTransaction())
			{
				foreach (var sql in (string[])["DELETE FROM base WHERE repo = $repo", "UPDATE repo SET baselined = 0 WHERE name = $repo"])
				{
					using (var command = Command(connection, sql, ("$repo", repo)))
					{
						command.Transaction = transaction;
						command.ExecuteNonQuery();
					}
				}

				transaction.Commit();
			}
		}
	}

	public ProjectSettings GetSettings(string repo)
	{
		var json = new JsonObject();
		using (var connection = Open())
		{
			using (var command = Command(connection, "SELECT key, value FROM setting WHERE repo = $repo", ("$repo", repo)))
			{
				using (var reader = command.ExecuteReader())
				{
					while (reader.Read())
					{
						json[reader.GetString(0)] = JsonNode.Parse(reader.GetString(1));
					}
				}
			}
		}

		return json.Deserialize<ProjectSettings>(JsonSerializerOptions.Web) ?? ProjectSettings.Default;
	}

	public ProjectSettings SaveSettings(string repo, ProjectSettings settings)
	{
		// Serializing the record yields every non-null property plus the Extra keys, camelCase.
		var rows = JsonSerializer.SerializeToElement(settings, JsonSerializerOptions.Web).EnumerateObject()
			.Where(p => p.Value.ValueKind != JsonValueKind.Null)
			.ToList();
		using (var connection = Open())
		{
			using (var transaction = connection.BeginTransaction())
			{
				using (var delete = Command(connection, "DELETE FROM setting WHERE repo = $repo", ("$repo", repo)))
				{
					delete.Transaction = transaction;
					delete.ExecuteNonQuery();
				}

				foreach (var row in rows)
				{
					using (var insert = Command(connection, "INSERT INTO setting (repo, key, value) VALUES ($repo, $key, $value)", ("$repo", repo), ("$key", row.Name), ("$value", row.Value.GetRawText())))
					{
						insert.Transaction = transaction;
						insert.ExecuteNonQuery();
					}
				}

				transaction.Commit();
			}
		}

		return GetSettings(repo);
	}

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

	private SqliteConnection Open()
	{
		EnsureSchema();
		var connection = new SqliteConnection($"Data Source={_path};Pooling=True");
		connection.Open();
		return connection;
	}

	private void EnsureSchema()
	{
		lock (_initLock)
		{
			if (_initialized)
			{
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			using (var connection = new SqliteConnection($"Data Source={_path};Pooling=True"))
			{
				connection.Open();
				using (var pragma = Command(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"))
				{
					pragma.ExecuteNonQuery();
				}

				using (var version = Command(connection, "PRAGMA user_version"))
				{
					if (version.ExecuteScalar() is 0L)
					{
						using (var transaction = connection.BeginTransaction())
						{
							using (var create = Command(connection, CreateTables))
							{
								create.Transaction = transaction;
								create.ExecuteNonQuery();
							}

							using (var setVersion = Command(connection, "PRAGMA user_version=1"))
							{
								setVersion.Transaction = transaction;
								setVersion.ExecuteNonQuery();
							}

							transaction.Commit();
						}
					}
				}
			}

			_initialized = true;
		}
	}
}
