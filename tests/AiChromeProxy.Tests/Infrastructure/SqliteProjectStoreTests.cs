using System.Text.Json;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Projects;
using AiChromeProxy.Tests.Tray;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class SqliteProjectStoreTests : IDisposable
{
	private readonly string _path = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"), "aicp.db");

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		var folder = Path.GetDirectoryName(_path)!;
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void NewDatabase_WalAndSchemaVersion1_NothingStored()
	{
		var store = Create();

		Assert.False(store.IsBaselined("r"));
		Assert.Empty(store.GetBases("r"));
		Assert.Equal(ProjectSettings.Default, store.GetSettings("r"));
		Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
		Assert.Equal(1L, Scalar("PRAGMA user_version"));
	}

	[Fact]
	public void Bases_SetReplaceRemove_PerRepo_PathIgnoresCase()
	{
		var store = Create();
		store.SetBases("r", [new("a.txt", "1"), new("b.txt", "2")]);
		store.SetBases("r", [new("A.TXT", "3"), new("b.txt", null)]);
		store.SetBases("other", [new("a.txt", "9")]);

		var bases = Create().GetBases("r"); // a second instance reads the same file

		Assert.Equal("3", Assert.Single(bases).Value);
		Assert.Equal("3", bases["a.txt"]);
	}

	[Fact]
	public void Baselined_Persists()
	{
		Create().SetBaselined("r");

		Assert.True(Create().IsBaselined("r"));
		Assert.False(Create().IsBaselined("s"));
	}

	[Fact]
	public void Reset_RemovesBasesAndBaseline_KeepsSettingsAndOtherRepos()
	{
		var store = Create();
		store.SetBaselined("r");
		store.SetBases("r", [new("a.txt", "1"), new("b.txt", "2")]);
		store.SaveSettings("r", new ProjectSettings { Excludes = "docs/" });
		store.SetBaselined("other");
		store.SetBases("other", [new("a.txt", "9")]);

		store.Reset("r");
		Create().Reset("never-synced");

		Assert.False(Create().IsBaselined("r"));
		Assert.Empty(Create().GetBases("r"));
		Assert.Equal("docs/", Create().GetSettings("r").Excludes);
		Assert.True(Create().IsBaselined("other"));
		Assert.Equal("9", Assert.Single(Create().GetBases("other")).Value);
	}

	[Fact]
	public void Settings_RoundTrip_UnknownKeysKept_NullRemoves()
	{
		var store = Create();
		var extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"model":"opus"}""");
		store.SaveSettings("r", new ProjectSettings { Excludes = "docs/", ApplyServerChanges = false, Extra = extra });

		var read = Create().GetSettings("r");
		Assert.Equal("docs/", read.Excludes);
		Assert.False(read.ApplyServerChangesOrDefault);
		Assert.Equal("opus", read.Extra!["model"].GetString());

		store.SaveSettings("r", read with { Excludes = null });
		Assert.Null(Create().GetSettings("r").Excludes);
	}

	[Fact]
	public void Defaults_DataDirOrContentRoot_ConfiguredWins()
	{
		var dataDir = new DataDirectory(@"C:\data");
		Assert.Equal(@"C:\data\aicp.db", ProjectsOptions.ResolveDatabase(null, dataDir, @"C:\app"));
		Assert.Equal(@"C:\app\data\aicp.db", ProjectsOptions.ResolveDatabase("", null, @"C:\app"));
		Assert.Equal(@"C:\app\x.db", ProjectsOptions.ResolveDatabase("x.db", dataDir, @"C:\app"));
	}

	[Fact]
	public void CrashDuringSchemaMigration_RecoveryOnNextOpen()
	{
		// Simulate a crash: create database with only the base table and user_version=0
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		using (var connection = new SqliteConnection($"Data Source={_path}"))
		{
			connection.Open();
			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandText = "CREATE TABLE base (repo TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, sha256 TEXT NOT NULL, PRIMARY KEY (repo, path))";
				cmd.ExecuteNonQuery();
			}
		}

		SqliteConnection.ClearAllPools();

		// Next store instance should recover: create missing tables and set version to 1
		var store = Create();
		store.SetBases("r", [new("a.txt", "1")]);
		var bases = store.GetBases("r");

		Assert.Single(bases);
		Assert.Equal("1", bases["a.txt"]);
		Assert.Equal(1L, Scalar("PRAGMA user_version"));
	}

	private SqliteProjectStore Create() => new(Options.Create(new ProjectsOptions { Database = _path }));

	private object? Scalar(string sql)
	{
		using (var connection = new SqliteConnection($"Data Source={_path}"))
		{
			connection.Open();
			using (var command = connection.CreateCommand())
			{
				command.CommandText = sql;
				return command.ExecuteScalar();
			}
		}
	}
}
