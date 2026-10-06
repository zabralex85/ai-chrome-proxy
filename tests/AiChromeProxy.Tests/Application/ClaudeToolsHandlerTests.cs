using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

/// <summary><c>agent.tools.get</c> / <c>agent.tools.check</c> with a scripted probe.</summary>
public sealed class ClaudeToolsHandlerTests : IDisposable
{
	private const string Repo = "My_Repo";
	private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
	private readonly MemoryProjectStore _projects = new();
	private readonly Probe _probe = new();
	private readonly ClaudeToolsHandler _get;
	private readonly ClaudeToolsHandler _check;
	private readonly ClaudeToolsSnapshot _old = new([new ClaudeMcpServer("old", "user", "connected")], [], DateTimeOffset.UnixEpoch, ClaudeToolsSnapshot.FromRun);

	public ClaudeToolsHandlerTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, Repo));
		var mirror = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
		_get = new ClaudeToolsHandler(MessageTypes.AgentToolsGet, _projects, mirror, _probe, _time, new ListLogger<ClaudeToolsHandler>());
		_check = new ClaudeToolsHandler(MessageTypes.AgentToolsCheck, _projects, mirror, _probe, _time, new ListLogger<ClaudeToolsHandler>());
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	[Fact]
	public void Types_AreGetAndCheck()
	{
		Assert.Equal([MessageTypes.AgentToolsGet, MessageTypes.AgentToolsCheck], ClaudeToolsHandler.Types);
		Assert.Equal((MessageTypes.AgentToolsGet, MessageTypes.AgentToolsCheck), (_get.Type, _check.Type));
	}

	[Fact]
	public async Task Get_MergesTheStoredSnapshotWithTheSettings_SanitizedRepo()
	{
		_projects.SaveToolsSnapshot(Repo, _old);
		_projects.SaveSettings(Repo, new ProjectSettings { AgentDisabledMcpServers = ["old"] });

		var reply = await _get.HandleAsync(Request(MessageTypes.AgentToolsGet, "My Repo", "c7"), new Client().Context, Ct);

		Assert.Equal((MessageTypes.AgentTools, "c7"), (reply!.Type, reply.CorrelationId));
		var payload = Read(reply);
		Assert.Equal((Repo, DateTimeOffset.UnixEpoch, ClaudeToolsSnapshot.FromRun, null), (payload.Repo, payload.CheckedAt, payload.From, payload.Error));
		Assert.Equal(new ClaudeMcpServerRow("old", "user", ClaudeToolStatuses.Off, false), Assert.Single(payload.Servers));
	}

	[Fact]
	public async Task Get_NothingStored_EmptyWithoutTime()
	{
		var payload = Read((await _get.HandleAsync(Request(MessageTypes.AgentToolsGet, Repo), new Client().Context, Ct))!);

		Assert.Equal((0, 0, (DateTimeOffset?)null), (payload.Servers.Count, payload.Plugins.Count, payload.CheckedAt));
	}

	[Fact]
	public async Task Handle_BadRepoOrPayload_BadRequest()
	{
		var context = new Client().Context;

		Assert.Equal(ErrorCodes.BadRequest, (await Assert.ThrowsAsync<EnvelopeException>(() => _get.HandleAsync(Request(MessageTypes.AgentToolsGet, " "), context, Ct))).Code);
		Assert.Equal(ErrorCodes.BadRequest, (await Assert.ThrowsAsync<EnvelopeException>(() => _check.HandleAsync(new Envelope(MessageTypes.AgentToolsCheck, JsonSerializer.SerializeToElement("x")), context, Ct))).Code);
		Assert.Equal(ErrorCodes.BadRequest, (await Assert.ThrowsAsync<EnvelopeException>(() => _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, "not-synced"), context, Ct))).Code);
		Assert.Equal(0, _probe.Calls);
	}

	[Fact]
	public async Task Check_RepliesLater_StampsAndSavesTheSnapshot_InTheMirror()
	{
		var client = new Client();

		Assert.Null(await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo, "c1"), client.Context, Ct));
		var folder = await _probe.Started.Task.WaitAsync(Wait, Ct);
		_probe.Answer.SetResult((new ClaudeToolsSnapshot([new ClaudeMcpServer("blender", null, "failed", "ECONNREFUSED")], [], default, ClaudeToolsSnapshot.FromCheck), null));
		var reply = await client.Reply.Task.WaitAsync(Wait, Ct);

		Assert.Equal(Path.Combine(_root, Repo), folder);
		Assert.Equal((MessageTypes.AgentTools, "c1"), (reply.Type, reply.CorrelationId));
		var payload = Read(reply);
		Assert.Equal((_time.GetUtcNow(), ClaudeToolsSnapshot.FromCheck, null), (payload.CheckedAt!.Value, payload.From, payload.Error));
		Assert.Equal(new ClaudeMcpServerRow("blender", null, ClaudeToolStatuses.Failed, true, Reason: "ECONNREFUSED"), Assert.Single(payload.Servers));
		Assert.Equal(_time.GetUtcNow(), _projects.GetToolsSnapshot(Repo)!.CheckedAt);
	}

	[Fact]
	public async Task Check_Fails_KeepsTheOldSnapshot_WithTheError()
	{
		_projects.SaveToolsSnapshot(Repo, _old);
		_probe.Answer.SetResult((null, "Check timed out after 60 s."));
		var client = new Client();

		await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo), client.Context, Ct);
		var payload = Read(await client.Reply.Task.WaitAsync(Wait, Ct));

		Assert.Equal("Check timed out after 60 s.", payload.Error);
		Assert.Equal((DateTimeOffset.UnixEpoch, ClaudeToolsSnapshot.FromRun), (payload.CheckedAt!.Value, payload.From));
		Assert.Equal("old", Assert.Single(payload.Servers).Name);
		Assert.Same(_old, _projects.GetToolsSnapshot(Repo));
	}

	[Fact]
	public async Task Check_WhileOneRuns_SharesItsResult_ThenANewOneStarts()
	{
		var first = new Client();
		var second = new Client();

		await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo, "a"), first.Context, Ct);
		await _probe.Started.Task.WaitAsync(Wait, Ct);
		await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, "My Repo", "b"), second.Context, Ct);
		_probe.Answer.SetResult((null, "Check failed: boom"));

		Assert.Equal("a", (await first.Reply.Task.WaitAsync(Wait, Ct)).CorrelationId);
		var shared = await second.Reply.Task.WaitAsync(Wait, Ct);
		Assert.Equal(("b", "Check failed: boom"), (shared.CorrelationId, Read(shared).Error));
		Assert.Equal(1, _probe.Calls);

		var third = new Client();
		await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo), third.Context, Ct);
		await third.Reply.Task.WaitAsync(Wait, Ct);
		Assert.Equal(2, _probe.Calls);
	}

	[Fact]
	public async Task Get_ProjectServersCarryTheirMcpJsonEntry()
	{
		await File.WriteAllTextAsync(Path.Combine(_root, Repo, McpJson.FileName), """{"mcpServers":{"team-db":{"command":"npx","args":["-y","db-mcp"]}}}""", Ct);
		_projects.SaveToolsSnapshot(Repo, new ClaudeToolsSnapshot([new ClaudeMcpServer("team-db", null, ClaudeToolStatuses.Pending)], [], DateTimeOffset.UnixEpoch, ClaudeToolsSnapshot.FromCheck));

		var row = Assert.Single(Read((await _get.HandleAsync(Request(MessageTypes.AgentToolsGet, Repo), new Client().Context, Ct))!).Servers);

		Assert.Equal(("npx -y db-mcp", McpJson.Hash(System.Text.Json.Nodes.JsonNode.Parse("""{"command":"npx","args":["-y","db-mcp"]}"""))), (row.Command, row.EntryHash));
	}

	[Fact]
	public async Task Check_OtherRepoWaitsForTheRunningProbe_BothReply()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Other"));
		var probe = new GatedProbe();
		var check = new ClaudeToolsHandler(MessageTypes.AgentToolsCheck, _projects, new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root })), probe, _time, new ListLogger<ClaudeToolsHandler>());
		var first = new Client();
		var second = new Client();

		await check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo), first.Context, Ct);
		Assert.True(await probe.Started.WaitAsync(Wait, Ct));
		await check.HandleAsync(Request(MessageTypes.AgentToolsCheck, "Other"), second.Context, Ct);
		Assert.False(await probe.Started.WaitAsync(TimeSpan.FromMilliseconds(300), Ct));

		probe.Go.Release();
		Assert.Equal("done " + Repo, Read(await first.Reply.Task.WaitAsync(Wait, Ct)).Error);
		Assert.True(await probe.Started.WaitAsync(Wait, Ct));
		probe.Go.Release();
		Assert.Equal("done Other", Read(await second.Reply.Task.WaitAsync(Wait, Ct)).Error);
		Assert.Equal(1, probe.MaxRunning);
	}

	[Fact]
	public async Task Check_StoreFails_InternalErrorReply_GoneConnectionIgnored()
	{
		_projects.FailToolsSnapshot = true;
		_probe.Answer.SetResult((new ClaudeToolsSnapshot([], [], default, ClaudeToolsSnapshot.FromCheck), null));
		var client = new Client();
		var gone = new Client(fails: true);

		await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo, "c1"), client.Context, Ct);
		var reply = await client.Reply.Task.WaitAsync(Wait, Ct);
		await _check.HandleAsync(Request(MessageTypes.AgentToolsCheck, Repo), gone.Context, Ct);
		await gone.Reply.Task.WaitAsync(Wait, Ct);

		Assert.Equal((MessageTypes.Error, "c1", ErrorCodes.Internal), (reply.Type, reply.CorrelationId, reply.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web)!.Code));
	}

	private static Envelope Request(string type, string repo, string? correlationId = null) =>
		Envelope.Create(type, new ClaudeToolsRequest(repo), correlationId);

	private static ClaudeToolsPayload Read(Envelope reply) => reply.Payload.Deserialize<ClaudeToolsPayload>(JsonSerializerOptions.Web)!;

	/// <summary>A probe that reports its start and waits for the test's answer.</summary>
	private sealed class Probe : IClaudeToolsProbe
	{
		private int _calls;

		public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource<(ClaudeToolsSnapshot? Snapshot, string? Error)> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public int Calls => _calls;

		public Task<(ClaudeToolsSnapshot? Snapshot, string? Error)> CheckAsync(string folder)
		{
			Interlocked.Increment(ref _calls);
			Started.TrySetResult(folder);
			return Answer.Task;
		}
	}

	/// <summary>A probe that signals each start and answers once released, counting how many run at once.</summary>
	private sealed class GatedProbe : IClaudeToolsProbe
	{
		private readonly Lock _lock = new();
		private int _running;

		public SemaphoreSlim Started { get; } = new(0);

		public SemaphoreSlim Go { get; } = new(0);

		public int MaxRunning { get; private set; }

		public async Task<(ClaudeToolsSnapshot? Snapshot, string? Error)> CheckAsync(string folder)
		{
			lock (_lock)
			{
				MaxRunning = Math.Max(MaxRunning, ++_running);
			}

			Started.Release();
			await Go.WaitAsync();
			lock (_lock)
			{
				_running--;
			}

			return (null, "done " + Path.GetFileName(folder));
		}
	}

	/// <summary>A connection: the first envelope sent to it (or the attempt, when <c>fails</c>).</summary>
	private sealed class Client(bool fails = false)
	{
		public TaskCompletionSource<Envelope> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public EnvelopeContext Context => new("c", null, (e, _) =>
		{
			Reply.TrySetResult(e);
			return fails ? Task.FromException(new IOException("gone")) : Task.CompletedTask;
		});
	}
}
