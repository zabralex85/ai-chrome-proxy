using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Chat;
using AiChromeProxy.Infrastructure.Projects;
using AiChromeProxy.Tests.Application;
using AiChromeProxy.Tests.Tray;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class SqliteChatStoreTests : IDisposable
{
	private readonly string _path = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"), "aicp.db");
	private readonly StepClock _clock = new();

	public static TheoryData<bool> Stores => [false, true];

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
	public void VersionOneDatabase_UpgradesToTwo_KeepsBasesAndSettings()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		using (var connection = new SqliteConnection($"Data Source={_path}"))
		{
			connection.Open();
			using (var command = connection.CreateCommand())
			{
				command.CommandText = """
					CREATE TABLE repo (name TEXT PRIMARY KEY, baselined INTEGER NOT NULL DEFAULT 0);
					CREATE TABLE base (repo TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, sha256 TEXT NOT NULL, PRIMARY KEY (repo, path));
					CREATE TABLE setting (repo TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (repo, key));
					INSERT INTO repo VALUES ('r', 1);
					INSERT INTO base VALUES ('r', 'a.txt', 'abc');
					INSERT INTO setting VALUES ('r', 'agentModel', '"opus"');
					PRAGMA user_version=1;
					""";
				command.ExecuteNonQuery();
			}
		}

		var chat = Chat();
		var session = chat.CreateSession("r", "hello");
		var projects = new SqliteProjectStore(Options.Create(new ProjectsOptions { Database = _path }));

		Assert.True(projects.IsBaselined("r"));
		Assert.Equal("abc", projects.GetBases("r")["a.txt"]);
		Assert.Equal("opus", projects.GetSettings("r").AgentModel);
		Assert.Equal(session.Id, Assert.Single(chat.ListSessions("r")).Id);
		Assert.Equal(2L, Version());
	}

	[Fact]
	public void ChatStoreFirst_CreatesWholeSchema()
	{
		Chat().CreateSession("r", "t");
		var projects = new SqliteProjectStore(Options.Create(new ProjectsOptions { Database = _path }));

		Assert.False(projects.IsBaselined("r"));
		Assert.Equal(2L, Version());
	}

	[Fact]
	public void Sessions_NewestUpdatedFirst_PerRepo_NotRunning()
	{
		var chat = Chat();
		var a = chat.CreateSession("r", "a");
		var b = chat.CreateSession("r", "b");
		chat.CreateSession("other", "x");
		chat.Touch(a.Id);

		var sessions = chat.ListSessions("r");

		Assert.Equal([a.Id, b.Id], sessions.Select(s => s.Id));
		Assert.Equal("a", sessions[0].Title);
		Assert.All(sessions, s => Assert.False(s.Running));
		Assert.True(sessions[0].Updated > sessions[1].Updated);
		Assert.Equal("r", chat.SessionRepo(a.Id));
		Assert.Null(chat.SessionRepo("nope"));
	}

	[Fact]
	public void ClaudeSession_NullUntilSet_Persists()
	{
		var chat = Chat();
		var s = chat.CreateSession("r", "t");

		Assert.Null(chat.GetClaudeSession(s.Id));
		Assert.Null(chat.GetClaudeSession("nope"));
		chat.SetClaudeSession(s.Id, "claude-1");

		Assert.Equal("claude-1", Chat().GetClaudeSession(s.Id));
	}

	[Fact]
	public void Append_SeqIncreasesPerSession_AndTouches()
	{
		var chat = Chat();
		var s1 = chat.CreateSession("r", "1");
		var s2 = chat.CreateSession("r", "2");
		var before = chat.ListSessions("r").Single(s => s.Id == s1.Id).Updated;

		var first = chat.Append(s1.Id, [Message(s1.Id, "a"), Message(s1.Id, "b")]);
		var other = chat.Append(s2.Id, [Message(s2.Id, "c")]);
		var next = chat.Append(s1.Id, [Message(s1.Id, "d")]);

		Assert.Equal([1L, 2L], first.Select(e => e.Seq));
		Assert.Equal(1L, other[0].Seq);
		Assert.Equal(3L, next[0].Seq);
		Assert.True(chat.ListSessions("r").Single(s => s.Id == s1.Id).Updated > before);
		Assert.Equal(s1.Id, chat.ListSessions("r")[0].Id);
	}

	[Fact]
	public async Task Append_Concurrent_NoDuplicateSeq()
	{
		var chat = Chat();
		var s = chat.CreateSession("r", "t");

		await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
		{
			for (var n = 0; n < 10; n++)
			{
				chat.Append(s.Id, [Message(s.Id, $"{i}-{n}")]);
			}
		})));

		var all = chat.Read(s.Id, 0, int.MaxValue).Events;
		Assert.Equal(Enumerable.Range(1, 80).Select(i => (long)i), all.Select(e => e.Seq));
	}

	[Fact]
	public void Append_TextDelta_Refused_NothingStored()
	{
		var chat = Chat();
		var s = chat.CreateSession("r", "t");

		Assert.Throws<ArgumentException>(() => chat.Append(s.Id, [Message(s.Id, "ok"), new ChatEvent(s.Id, "run", 0, ChatEventKinds.Text, Text: "d")]));

		Assert.Empty(chat.Read(s.Id, 0, 1000).Events);
	}

	[Fact]
	public void Read_RoundTripsEveryField_AfterSeq()
	{
		var chat = Chat();
		var s = chat.CreateSession("r", "t");
		var result = new ChatEvent(s.Id, "run", 0, ChatEventKinds.Result, Ok: true, CostUsd: 0.12m, DurationMs: 42, Error: "é\"");
		chat.Append(s.Id, [Message(s.Id, "a"), result]);

		var (events, final) = chat.Read(s.Id, 1, 100_000);

		Assert.True(final);
		Assert.Equal(result with { Seq = 2 }, Assert.Single(events));
		Assert.Empty(chat.Read(s.Id, 2, 1000).Events);
		Assert.True(chat.Read(s.Id, 2, 1000).Final);
	}

	[Fact]
	public void Read_PagesByBytes_AtLeastOneEvent_FinalOnLast()
	{
		var chat = Chat();
		var s = chat.CreateSession("r", "t");
		var stored = chat.Append(s.Id, [Message(s.Id, new string('a', 100)), Message(s.Id, new string('b', 100)), Message(s.Id, new string('c', 100))]);
		var size = JsonSerializer.SerializeToUtf8Bytes(stored[0], JsonSerializerOptions.Web).Length;

		var page1 = chat.Read(s.Id, 0, (2 * size) + 1);
		var page2 = chat.Read(s.Id, page1.Events[^1].Seq, (2 * size) + 1);
		var tiny = chat.Read(s.Id, 0, 1);

		Assert.Equal([1L, 2L], page1.Events.Select(e => e.Seq));
		Assert.False(page1.Final);
		Assert.Equal([3L], page2.Events.Select(e => e.Seq));
		Assert.True(page2.Final);
		Assert.Equal([1L], tiny.Events.Select(e => e.Seq));
		Assert.False(tiny.Final);
		Assert.Equal(stored, chat.Read(s.Id, 0, int.MaxValue).Events);
	}

	[Fact]
	public void Read_PageExactlyTwoEvents_ReturnsBoth_SeqCrossesTen()
	{
		var chat = Chat();
		var s = chat.CreateSession("r", "t");
		var stored = chat.Append(s.Id, Enumerable.Range(0, 11).Select(i => Message(s.Id, "same")).ToList());
		var size = (long)JsonSerializer.SerializeToUtf8Bytes(stored[8], JsonSerializerOptions.Web).Length; // seq 9
		var next = JsonSerializer.SerializeToUtf8Bytes(stored[9], JsonSerializerOptions.Web).Length; // seq 10, one digit longer

		var page = chat.Read(s.Id, 8, (int)(size + next));

		Assert.Equal([9L, 10L], page.Events.Select(e => e.Seq));
		Assert.False(page.Final);
		Assert.Equal([9L], chat.Read(s.Id, 8, (int)(size + next) - 1).Events.Select(e => e.Seq));
		Assert.Equal(11L, stored[^1].Seq);
	}

	[Theory]
	[MemberData(nameof(Stores))]
	public void UnknownSession_SameContractInBothStores(bool memory)
	{
		IChatStore chat = memory ? new MemoryChatStore(_clock) : Chat();

		Assert.Throws<KeyNotFoundException>(() => chat.Append("nope", [Message("nope", "a")]));
		Assert.Throws<KeyNotFoundException>(() => chat.SetClaudeSession("nope", "c"));
		Assert.Throws<KeyNotFoundException>(() => chat.Touch("nope"));
		Assert.Equal((Array.Empty<ChatEvent>(), true), (chat.Read("nope", 0, 1000).Events.ToArray(), chat.Read("nope", 0, 1000).Final));
		Assert.Null(chat.SessionRepo("nope"));
		Assert.Null(chat.GetClaudeSession("nope"));
	}

	[Fact]
	public void Append_UnknownSession_LeavesNoOrphanRows()
	{
		var chat = Chat();
		Assert.Throws<KeyNotFoundException>(() => chat.Append("nope", [Message("nope", "a")]));

		using (var connection = new SqliteConnection($"Data Source={_path}"))
		{
			connection.Open();
			using (var command = connection.CreateCommand())
			{
				command.CommandText = "SELECT COUNT(*) FROM chat_event";
				Assert.Equal(0L, (long)command.ExecuteScalar()!);
			}
		}
	}

	[Fact]
	public void MemoryChatStore_BehavesTheSame()
	{
		var chat = new MemoryChatStore(_clock);
		var a = chat.CreateSession("r", "a");
		var b = chat.CreateSession("r", "b");
		chat.Touch(a.Id);
		var stored = chat.Append(a.Id, [Message(a.Id, "x"), Message(a.Id, "y")]);
		chat.SetClaudeSession(a.Id, "c");

		Assert.Equal([a.Id, b.Id], chat.ListSessions("r").Select(s => s.Id));
		Assert.Equal([1L, 2L], stored.Select(e => e.Seq));
		Assert.Equal("c", chat.GetClaudeSession(a.Id));
		Assert.Equal("r", chat.SessionRepo(a.Id));
		Assert.False(chat.Read(a.Id, 0, 1).Final);
		Assert.True(chat.Read(a.Id, 1, 100_000).Final);
		Assert.Throws<ArgumentException>(() => chat.Append(a.Id, [new ChatEvent(a.Id, "run", 0, ChatEventKinds.Text, Text: "d")]));
	}

	private static ChatEvent Message(string sessionId, string text) => new(sessionId, "run", 0, ChatEventKinds.Message, Text: text);

	private SqliteChatStore Chat() => new(Options.Create(new ProjectsOptions { Database = _path }), _clock);

	private long Version()
	{
		using (var connection = new SqliteConnection($"Data Source={_path}"))
		{
			connection.Open();
			using (var command = connection.CreateCommand())
			{
				command.CommandText = "PRAGMA user_version";
				return (long)command.ExecuteScalar()!;
			}
		}
	}

	private sealed class StepClock : TimeProvider
	{
		private long _ms = 1_700_000_000_000;

		public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Add(ref _ms, 10));
	}
}
