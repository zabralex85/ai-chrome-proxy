using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Domain.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Projects;

/// <summary>SQLite (WAL) implementation of <see cref="IProjectStore"/>: one connection per call, schema kept by <see cref="AicpDatabase"/>.</summary>
public sealed class SqliteProjectStore : IProjectStore
{
	private readonly string _path;

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

	public void ForgetBases(string repo)
	{
		using (var connection = Open())
		{
			using (var command = Command(connection, "DELETE FROM base WHERE repo = $repo", ("$repo", repo)))
			{
				command.ExecuteNonQuery();
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

	private SqliteConnection Open() => AicpDatabase.Open(_path);
}
