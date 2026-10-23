using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Projects;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Projects;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Client;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

/// <summary><c>repos.list</c>, <c>mirror.list</c> and <c>mirror.read</c> over a real mirror in a temp folder.</summary>
public sealed class MirrorBrowseHandlerTests : IDisposable
{
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
	private readonly MemoryProjectStore _projects = new();
	private readonly MemoryChatStore _chats;
	private readonly FakeAgentRunner _runner = new();
	private readonly ChatService _chat;
	private readonly SyncSessions _sessions;
	private readonly EnvelopeRouter _router;

	public MirrorBrowseHandlerTests()
	{
		Directory.CreateDirectory(_root);
		var store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
		_chats = new MemoryChatStore(_time);
		_chat = new ChatService(_chats, store, _projects, _runner, _time, new ListLogger<ChatService>());
		_sessions = new SyncSessions(store, _projects, new ListLogger<SyncSession>(), TimeProvider.System, new FakeMirrorWatcher());
		_router = new EnvelopeRouter([.. MirrorBrowseHandler.Types.Select(t => new MirrorBrowseHandler(t, store, _projects, _chats, _chat, _sessions))]);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private static EnvelopeContext Context => new("c1", null, (_, _) => Task.CompletedTask);

	public void Dispose()
	{
		_sessions.Close("c1");
		_chat.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public void Types_AreTheThreeRequests() =>
		Assert.Equal([MessageTypes.ReposList, MessageTypes.MirrorList, MessageTypes.MirrorRead], MirrorBrowseHandler.Types);

	[Fact]
	public async Task ReposList_ActivityRunningAndOpen_NewestFirst()
	{
		Write("quiet", "a.txt", "a");
		Write("busy", "a.txt", "a");
		Write("open", "a.txt", "a");
		_chats.CreateSession("open", "older");
		_time.Advance(TimeSpan.FromMinutes(1));
		_chats.CreateSession("busy", "newer");
		await _sessions.Get("c1").HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("open"), "o1"), Context, Ct);
		_chat.Send(new ChatSendPayload("busy", null, "work"), Context);
		var process = await _runner.NextAsync();

		var repos = Read<ReposPayload>(await RequestAsync(MessageTypes.ReposList, new { }));

		Assert.False(repos.Truncated);
		Assert.Equal(
			[
				new RepoInfo("busy", _time.GetUtcNow(), true, false),
				new RepoInfo("open", _time.GetUtcNow() - TimeSpan.FromMinutes(1), false, true),
				new RepoInfo("quiet", null, false, false),
			],
			repos.Repos);
		process.Exit();
	}

	[Fact]
	public void ReposPage_CutOffUnderTheByteLimit_FlagsTruncated()
	{
		var repos = Enumerable.Range(0, 400).Select(i => new RepoInfo($"{i:D3}" + new string('r', RepoName.MaxLength - 3), DateTimeOffset.MaxValue, true, true)).ToList();

		var page = MirrorBrowseHandler.ReposPage(repos);

		Assert.True(page.Truncated);
		Assert.InRange(page.Repos.Count, 100, 399);
		Assert.True(ManifestPlannerTests.WireSize(Envelope.Create(MessageTypes.Repos, page, Guid.NewGuid().ToString("N"))) < ManifestPlannerTests.SignalRLimit - 4096);
	}

	[Fact]
	public async Task MirrorList_PagesInPathOrder_LeavesOutExcluded()
	{
		Write("r", ".gitignore", "*.log\n");
		Write("r", "b.txt", "bb");
		Write("r", "a/z.cs", "z");
		Write("r", "debug.log", "x");
		Write("r", "secret.txt", "s");
		_projects.SaveSettings("r", new ProjectSettings { Excludes = "secret.txt" });

		var page = Read<MirrorFilesPayload>(await RequestAsync(MessageTypes.MirrorList, new MirrorListPayload("r")));

		Assert.Equal([".gitignore", "a/z.cs", "b.txt"], page.Files.Select(f => f.Path));
		Assert.Equal(2, page.Files[2].Size);
		Assert.Null(page.Next);
	}

	[Fact]
	public void FilesPage_EntryAndByteBounds_NextIsTheLastPath()
	{
		var files = Enumerable.Range(0, 1_200).Select(i => new MirrorFile($"{i:D4}/" + new string('ж', 250), long.MaxValue, DateTimeOffset.MaxValue)).ToList();

		var first = MirrorBrowseHandler.FilesPage("r", files, null);
		var second = MirrorBrowseHandler.FilesPage("r", files, first.Next);

		Assert.True(first.Files.Count <= SyncLimits.MaxPageEntries);
		Assert.Equal(first.Files[^1].Path, first.Next);
		Assert.Equal(files[first.Files.Count].Path, second.Files[0].Path);
		Assert.True(ManifestPlannerTests.WireSize(Envelope.Create(MessageTypes.MirrorFiles, first, Guid.NewGuid().ToString("N"))) < ManifestPlannerTests.SignalRLimit - 4096);

		var small = files.Take(3).ToList();
		Assert.Null(MirrorBrowseHandler.FilesPage("r", small, null).Next);
		Assert.Empty(MirrorBrowseHandler.FilesPage("r", small, small[^1].Path).Files);
	}

	[Fact]
	public async Task MirrorList_MissingRepo_NotFound()
	{
		var error = await ErrorAsync(MessageTypes.MirrorList, new MirrorListPayload("gone"));

		Assert.Equal((ErrorCodes.NotFound, "This project is no longer on the server."), (error.Code, error.Message));
	}

	[Fact]
	public async Task MirrorRead_ChunksLastAndHash_ExactBoundaryEndsWithAnEmptyChunk()
	{
		var content = Enumerable.Range(0, SyncLimits.ChunkSize).Select(i => (byte)i).ToArray();
		Directory.CreateDirectory(Path.Combine(_root, "r"));
		File.WriteAllBytes(Path.Combine(_root, "r", "f.bin"), content);

		var first = Read<SyncDataPayload>(await RequestAsync(MessageTypes.MirrorRead, new MirrorReadPayload("r", "f.bin", 0)));
		var last = Read<SyncDataPayload>(await RequestAsync(MessageTypes.MirrorRead, new MirrorReadPayload("r", "f.bin", SyncLimits.ChunkSize)));

		Assert.Equal(content, SyncData.Decode(first.Data));
		Assert.Equal((false, (string?)null), (first.Last, first.Sha256));
		Assert.Equal((0, true), (SyncData.Decode(last.Data).Length, last.Last));
		Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), last.Sha256);
	}

	[Theory]
	[InlineData("debug.log", 0, ErrorCodes.BadRequest)]
	[InlineData("../x.txt", 0, ErrorCodes.BadRequest)]
	[InlineData("a.txt", -1, ErrorCodes.BadRequest)]
	[InlineData("missing.txt", 0, ErrorCodes.NotFound)]
	public async Task MirrorRead_Refusals(string path, long offset, string code)
	{
		Write("r", ".gitignore", "*.log\n");
		Write("r", "a.txt", "a");
		Write("r", "debug.log", "x");

		var error = await ErrorAsync(MessageTypes.MirrorRead, new MirrorReadPayload("r", path, offset));

		Assert.Equal(code, error.Code);
	}

	[Fact]
	public async Task BadRepoName_BadRequest()
	{
		var error = await ErrorAsync(MessageTypes.MirrorList, new MirrorListPayload(" "));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private void Write(string repo, string path, string text)
	{
		var full = Path.Combine(_root, repo, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text);
	}

	private async Task<Envelope> RequestAsync<T>(string type, T payload) =>
		(await _router.RouteAsync(Envelope.Create(type, payload, "q1"), Context, Ct))!;

	private async Task<ErrorPayload> ErrorAsync<T>(string type, T payload)
	{
		var reply = await RequestAsync(type, payload);
		Assert.Equal(MessageTypes.Error, reply.Type);
		return Read<ErrorPayload>(reply);
	}
}
