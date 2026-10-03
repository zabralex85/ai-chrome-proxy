using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Application;

/// <summary>The server's three-way reconciliation (bases, pushes, fetch, ack) against a real mirror in a temp folder.</summary>
public sealed class SyncSessionBackChannelTests : IDisposable
{
	private const string Repo = "repo";

	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _repoRoot;
	private readonly ListLogger<SyncSession> _logger = new();
	private readonly MemoryProjectStore _projects = new();
	private readonly List<Envelope> _pushed = [];
	private readonly SyncSession _session;

	public SyncSessionBackChannelTests()
	{
		_repoRoot = Path.Combine(_root, Repo);
		Directory.CreateDirectory(_repoRoot);
		_session = new SyncSession(new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root })), _projects, _logger, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private EnvelopeContext Context => new("conn-1", null, (e, _) =>
	{
		_pushed.Add(e);
		return Task.CompletedTask;
	});

	private IReadOnlyList<RemoteChange> Pushes => [.. _pushed.SelectMany(e => Read<SyncRemotePayload>(e).Changes)];

	public void Dispose()
	{
		_session.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public async Task FirstFullManifest_ClientWins_ThenBaselined_BasesSet()
	{
		Write("a.txt", "old");
		Write("b.txt", "b");
		Write("c.txt", "c");
		await OpenAsync();

		var need = await NeedAsync(Manifest(true, Entry("a.txt", "new"), Entry("c.txt", "c")));

		Assert.Equal(["a.txt"], need);
		Assert.False(File.Exists(PathOf("b.txt")));
		Assert.True(_projects.IsBaselined(Repo));
		Assert.Equal(1, _projects.SetBasesCalls);
		Assert.Equal(
			[("a.txt", Sha("old")), ("c.txt", Sha("c"))],
			_projects.GetBases(Repo).Select(b => (b.Key, b.Value)).OrderBy(b => b.Key, StringComparer.Ordinal));
		Assert.Empty(_pushed);

		Assert.Equal(MessageTypes.SyncStored, (await HandleAsync(Chunk("a.txt", "new")))!.Type);

		Assert.Equal(Sha("new"), _projects.GetBases(Repo)["a.txt"]);
	}

	[Fact]
	public async Task ServerEdit_PeriodicManifest_PushesInsteadOfUpload()
	{
		Baseline(("a.txt", "v1"));
		Write("a.txt", "v2");
		await OpenAsync();

		var need = await NeedAsync(Manifest(true, Entry("a.txt", "v1")));

		Assert.Empty(need);
		var push = Assert.Single(_pushed);
		Assert.Equal(MessageTypes.SyncRemote, push.Type);
		Assert.Null(push.CorrelationId);
		Assert.Equal(Repo, Read<SyncRemotePayload>(push).Repo);
		Assert.Equal([new RemoteChange("a.txt", Sha("v2"), 2, Sha("v1"))], Pushes);
		Assert.Equal("v2", File.ReadAllText(PathOf("a.txt")));
		Assert.Equal(Sha("v1"), _projects.GetBases(Repo)["a.txt"]);
	}

	[Fact]
	public async Task ServerCreatedFile_NotInManifest_PushedNotDeleted()
	{
		Baseline(("a.txt", "a"));
		Write("a.txt", "a");
		Write("new.txt", "n");
		await OpenAsync();

		await NeedAsync(Manifest(true, Entry("a.txt", "a")));

		Assert.True(File.Exists(PathOf("new.txt")));
		Assert.Equal([new RemoteChange("new.txt", Sha("n"), 1, null)], Pushes);
		Assert.Contains(_logger.Messages, m => m.Contains("Sync repo: manifest of 1 files, 0 to upload, 0 deleted", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ClientDeleted_MirrorUnchanged_DeletedAndBaseRemoved(bool delta)
	{
		Baseline(("a.txt", "a"), ("b.txt", "b"), ("gone.txt", "g"));
		Write("a.txt", "a");
		Write("b.txt", "b");
		await OpenAsync();

		await NeedAsync(delta
			? Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [], ["b.txt", "gone.txt"]))
			: Manifest(true, Entry("a.txt", "a")));

		Assert.False(File.Exists(PathOf("b.txt")));
		Assert.True(File.Exists(PathOf("a.txt")));
		Assert.Equal(["a.txt"], _projects.GetBases(Repo).Keys);
		Assert.Empty(_pushed);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExcludedMirrorFiles_NeverDeletedNorPushed(bool baselined)
	{
		if (baselined)
		{
			_projects.SetBaselined(Repo);
		}

		_projects.SaveSettings(Repo, new ProjectSettings { Excludes = "tmp/" });
		string[] excluded = ["bin/app.dll", "obj/x", "logs/a.log", "tmp/t.txt", ".env"];
		Write(".gitignore", "logs/\n");
		Write("a.txt", "a");
		Array.ForEach(excluded, p => Write(p, "server only"));
		await OpenAsync();

		await NeedAsync(Manifest(true, Entry(".gitignore", "logs/\n"), Entry("a.txt", "a")));
		await NeedAsync(Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [], ["tmp/t.txt"])));
		await _session.MirrorChangedAsync(null, Ct);
		await _session.MirrorChangedAsync(excluded, Ct);

		Assert.All(excluded, p => Assert.True(File.Exists(PathOf(p)), p));
		Assert.Empty(_pushed);
	}

	[Fact]
	public async Task BothChanged_DeltaUpsert_NoUpload_Pushed()
	{
		Baseline(("a.txt", "v1"));
		Write("a.txt", "server");
		await OpenAsync();

		var need = await NeedAsync(Delta([Entry("a.txt", "client")]));

		Assert.Empty(need);
		Assert.Equal([new RemoteChange("a.txt", Sha("server"), 6, Sha("v1"))], Pushes);
		Assert.Equal("server", File.ReadAllText(PathOf("a.txt")));
		await AssertError(ErrorCodes.NotFound, Chunk("a.txt", "client"));
	}

	[Fact]
	public async Task BothChanged_PendingUploadDropped()
	{
		// The client's earlier version was requested, then the server changed the file: that upload must not overwrite it.
		Baseline(("a.txt", "v1"));
		Write("a.txt", "v1");
		await OpenAsync();
		Assert.Equal(["a.txt"], await NeedAsync(Delta([Entry("a.txt", "client")])));
		Write("a.txt", "server");

		Assert.Empty(await NeedAsync(Delta([Entry("a.txt", "client2")])));

		await AssertError(ErrorCodes.NotFound, Chunk("a.txt", "client"));
		Assert.Equal("server", File.ReadAllText(PathOf("a.txt")));
	}

	[Fact]
	public async Task Fetch_ReturnsChunks_LastHasHash()
	{
		var content = RandomNumberGenerator.GetBytes(40_000);
		File.WriteAllBytes(PathOf("big.bin"), content);
		var exact = RandomNumberGenerator.GetBytes(SyncLimits.ChunkSize);
		File.WriteAllBytes(PathOf("exact.bin"), exact);
		await OpenAsync();

		var parts = new List<SyncDataPayload>();
		foreach (var offset in new[] { 0, 16_384, 32_768 })
		{
			var reply = await HandleAsync(Envelope.Create(MessageTypes.SyncFetch, new SyncFetchPayload(Repo, "big.bin", offset), $"f{offset}"));
			Assert.Equal(MessageTypes.SyncData, reply!.Type);
			Assert.Equal($"f{offset}", reply.CorrelationId);
			parts.Add(Read<SyncDataPayload>(reply));
		}

		Assert.Equal([16_384, 16_384, 7_232], parts.Select(p => SyncData.Decode(p.Data).Length));
		Assert.Equal([false, false, true], parts.Select(p => p.Last));
		Assert.Equal([null, null, Sha(content)], parts.Select(p => p.Sha256));
		Assert.Equal([0L, 16_384L, 32_768L], parts.Select(p => p.Offset));
		Assert.All(parts, p => Assert.Equal(("repo", "big.bin"), (p.Repo, p.Path)));
		Assert.Equal(content, parts.SelectMany(p => SyncData.Decode(p.Data)));

		var first = Read<SyncDataPayload>((await HandleAsync(Envelope.Create(MessageTypes.SyncFetch, new SyncFetchPayload(Repo, "exact.bin", 0))))!);
		var second = Read<SyncDataPayload>((await HandleAsync(Envelope.Create(MessageTypes.SyncFetch, new SyncFetchPayload(Repo, "exact.bin", SyncLimits.ChunkSize))))!);
		Assert.Equal((SyncLimits.ChunkSize, false, (string?)null), (SyncData.Decode(first.Data).Length, first.Last, first.Sha256));
		Assert.Equal((0, true, Sha(exact)), (SyncData.Decode(second.Data).Length, second.Last, second.Sha256));
	}

	[Fact]
	public async Task Fetch_Missing_NotFound()
	{
		await OpenAsync();

		await AssertError(ErrorCodes.NotFound, Envelope.Create(MessageTypes.SyncFetch, new SyncFetchPayload(Repo, "missing.txt", 0)));
	}

	[Theory]
	[InlineData("bin/app.dll", 0)]
	[InlineData("tmp/t.txt", 0)]
	[InlineData("../outside.txt", 0)]
	[InlineData(".git/config", 0)]
	[InlineData("", 0)]
	[InlineData("a.txt.0123abcd.aicp-tmp", 0)]
	[InlineData("a.txt", -1)]
	public async Task Fetch_IgnoredOrInvalidPath_BadRequest(string path, long offset)
	{
		_projects.SaveSettings(Repo, new ProjectSettings { Excludes = "tmp/" });
		Write("a.txt", "a");
		Write("bin/app.dll", "b");
		Write("tmp/t.txt", "t");
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncFetch, new SyncFetchPayload(Repo, path, offset)));
	}

	[Fact]
	public async Task Ack_SetsAndRemovesBase_EchoesPayload()
	{
		await OpenAsync();
		var ack = new SyncAckPayload(Repo, "a.txt", Sha("a"));

		var reply = await HandleAsync(Envelope.Create(MessageTypes.SyncAck, ack, "k1"));

		Assert.Equal((MessageTypes.SyncAck, "k1"), (reply!.Type, reply.CorrelationId));
		Assert.Equal(ack, Read<SyncAckPayload>(reply));
		Assert.Equal(Sha("a"), _projects.GetBases(Repo)["a.txt"]);

		await HandleAsync(Envelope.Create(MessageTypes.SyncAck, ack with { Sha256 = null }));

		Assert.Empty(_projects.GetBases(Repo));
	}

	[Theory]
	[InlineData("a.txt", "")]
	[InlineData("a.txt", "abc")]
	[InlineData("a.txt", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("../a.txt", null)]
	[InlineData("bin/app.dll", null)]
	[InlineData("tmp/t.txt", "0000000000000000000000000000000000000000000000000000000000000000")]
	public async Task Ack_BadHashOrExcludedPath_BadRequest(string path, string? sha256)
	{
		_projects.SaveSettings(Repo, new ProjectSettings { Excludes = "tmp/" });
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncAck, new SyncAckPayload(Repo, path, sha256)));

		Assert.Empty(_projects.GetBases(Repo));
	}

	[Fact]
	public async Task MirrorChanged_PushesOnlyWhenMirrorDiffersFromBase()
	{
		_projects.SetBaselined(Repo);
		await OpenAsync();
		Assert.Equal(["a.txt"], await NeedAsync(Delta([Entry("a.txt", "a")])));
		await HandleAsync(Chunk("a.txt", "a"));

		await _session.MirrorChangedAsync(["a.txt"], Ct);
		Assert.Empty(_pushed);

		Write("a.txt", "edited");
		await _session.MirrorChangedAsync(["a.txt"], Ct);
		Assert.Equal([new RemoteChange("a.txt", Sha("edited"), 6, Sha("a"))], Pushes);

		_pushed.Clear();
		await HandleAsync(Envelope.Create(MessageTypes.SyncAck, new SyncAckPayload(Repo, "gone.txt", Sha("g"))));
		await _session.MirrorChangedAsync(null, Ct);
		Assert.Equal(
			[new RemoteChange("a.txt", Sha("edited"), 6, Sha("a")), new RemoteChange("gone.txt", null, 0, Sha("g"))],
			Pushes.OrderBy(c => c.Path, StringComparer.Ordinal));
	}

	[Fact]
	public async Task MirrorChanged_FolderRenamed_PushesOldFilesDeletedAndNewOnesCreated()
	{
		Write("d/a.txt", "a");
		Write("d/sub/b.txt", "b");
		Baseline(("d/a.txt", "a"), ("d/sub/b.txt", "b"));
		await OpenAsync();
		Directory.Move(PathOf("d"), PathOf("e"));

		await _session.MirrorChangedAsync(["d", "e"], Ct);

		Assert.Equal(
			[
				new RemoteChange("d/a.txt", null, 0, Sha("a")),
				new RemoteChange("d/sub/b.txt", null, 0, Sha("b")),
				new RemoteChange("e/a.txt", Sha("a"), 1, null),
				new RemoteChange("e/sub/b.txt", Sha("b"), 1, null),
			],
			Pushes.OrderBy(c => c.Path, StringComparer.Ordinal));
	}

	[Fact]
	public async Task MirrorChanged_BeforeOpenOrAfterDispose_NoOp()
	{
		Write("a.txt", "a");

		await _session.MirrorChangedAsync(null, Ct);
		Assert.Null(_session.Repo);
		await OpenAsync();
		Assert.Equal(Repo, _session.Repo);
		_session.Dispose();
		await _session.MirrorChangedAsync(null, Ct);

		Assert.Empty(_pushed);
	}

	[Fact]
	public async Task MirrorChanged_LockedFile_Skipped_OthersPushed()
	{
		Write("locked.txt", "l");
		Write("b.txt", "b");
		_projects.SetBaselined(Repo);
		await OpenAsync();

		using (new FileStream(PathOf("locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			await _session.MirrorChangedAsync(["locked.txt", "b.txt"], Ct);
		}

		Assert.Equal(["b.txt"], Pushes.Select(c => c.Path));
	}

	[Fact]
	public async Task MirrorChanged_BeforeFirstFullManifest_PushesNothing()
	{
		Write("a.txt", "a");
		Write("d/b.txt", "b");
		await OpenAsync();

		await _session.MirrorChangedAsync(["a.txt", "d"], Ct);
		await _session.MirrorChangedAsync(null, Ct);

		Assert.Empty(_pushed);
	}

	[Fact]
	public async Task EmptyMirror_WithBases_StartsOver_EveryFileUploaded_NothingPushed()
	{
		Baseline(("a.txt", "a"), ("d/b.txt", "b"));
		Write("bin/app.dll", "server only, excluded: does not count");
		await OpenAsync();

		Assert.Equal(["a.txt"], await NeedAsync(Manifest(false, Entry("a.txt", "a"))));

		Assert.False(_projects.IsBaselined(Repo));
		Assert.Empty(_projects.GetBases(Repo));

		Assert.Equal(["d/b.txt"], await NeedAsync(Manifest(true, Entry("d/b.txt", "b"))));

		Assert.Empty(_pushed);
		Assert.True(_projects.IsBaselined(Repo));
		Assert.True(File.Exists(PathOf("bin/app.dll")));
		Assert.Single(
			_logger.Entries,
			e => e is (LogLevel.Warning, "Mirror of repo is empty while 2 files were synced; starting over: the browser's files are uploaded again"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MirrorEmptied_MirrorChanged_PushesNoDeletes_StartsOver(bool folderDeleted)
	{
		Write("a.txt", "a");
		Write("d/b.txt", "b");
		Baseline(("a.txt", "a"), ("d/b.txt", "b"));
		await OpenAsync();
		if (folderDeleted)
		{
			Directory.Delete(_repoRoot, recursive: true);
		}
		else
		{
			File.Delete(PathOf("a.txt"));
			File.Delete(PathOf("d/b.txt"));
		}

		await _session.MirrorChangedAsync(["a.txt", "d"], Ct);
		await _session.MirrorChangedAsync(null, Ct);

		Assert.Empty(_pushed);
		Assert.False(_projects.IsBaselined(Repo));
		Assert.Empty(_projects.GetBases(Repo));
	}

	[Fact]
	public async Task Delta_DeletesEverySyncedFile_ExcludedFilesDoNotCount_BadRequest()
	{
		Write("a.txt", "a");
		Write("bin/app.dll", "server only");
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [], ["a.txt"])));

		Assert.True(File.Exists(PathOf("a.txt")));
	}

	[Fact]
	public async Task ServerEditDuringUpload_NotOverwritten_Pushed()
	{
		Baseline(("a.txt", "v1"));
		Write("a.txt", "v1");
		await OpenAsync();
		Assert.Equal(["a.txt"], await NeedAsync(Delta([Entry("a.txt", "client")])));
		Write("a.txt", "server");

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", "client"));

		Assert.Equal("server", File.ReadAllText(PathOf("a.txt")));
		Assert.Equal([new RemoteChange("a.txt", Sha("server"), 6, Sha("v1"))], Pushes);
		Assert.Equal(Sha("v1"), _projects.GetBases(Repo)["a.txt"]);
		Assert.Empty(Directory.GetFiles(_repoRoot, "*.aicp-tmp"));
		await AssertError(ErrorCodes.NotFound, Chunk("a.txt", "client"));
	}

	[Fact]
	public async Task ServerEditDuringUpload_MirrorChanged_UploadDropped_Pushed()
	{
		Baseline(("a.txt", "v1"));
		Write("a.txt", "v1");
		await OpenAsync();
		Assert.Equal(["a.txt"], await NeedAsync(Delta([Entry("a.txt", "client")])));
		Assert.Null(await HandleAsync(Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, "a.txt", 0, SyncData.Encode("cli"u8.ToArray()), false))));
		Write("a.txt", "server");

		await _session.MirrorChangedAsync(["A.TXT"], Ct);

		Assert.Empty(Directory.GetFiles(_repoRoot, "*.aicp-tmp"));
		await AssertError(ErrorCodes.NotFound, Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, "a.txt", 3, SyncData.Encode("ent"u8.ToArray()), true)));
		Assert.Equal("server", File.ReadAllText(PathOf("a.txt")));
		Assert.Equal([new RemoteChange("A.TXT", Sha("server"), 6, Sha("v1"))], Pushes);
	}

	[Fact]
	public async Task UploadPendingBeforeBaseline_StillUploadAfterIt()
	{
		// The connection drops before the upload: the next (baselined) pass must still see a client change, not a conflict.
		Write("a.txt", "old");
		await OpenAsync();
		Assert.Equal(["a.txt"], await NeedAsync(Manifest(true, Entry("a.txt", "new"))));
		await OpenAsync();

		Assert.Equal(["a.txt"], await NeedAsync(Manifest(true, Entry("a.txt", "new"))));

		Assert.Empty(_pushed);
	}

	[Fact]
	public async Task FinalPage_LockedStaleFile_Skipped_PageSucceeds()
	{
		Write("a.txt", "a");
		Write("locked.txt", "l");
		await OpenAsync();

		using (new FileStream(PathOf("locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			Assert.Empty(await NeedAsync(Manifest(true, Entry("a.txt", "a"))));
		}

		Assert.True(File.Exists(PathOf("locked.txt")));
		Assert.Empty(_pushed);
		Assert.True(_projects.IsBaselined(Repo));
	}

	[Fact]
	public async Task Dispose_DuringPush_LaterPagesNotSent()
	{
		_projects.SetBaselined(Repo);
		for (var i = 0; i < SyncLimits.MaxPageEntries + 1; i++)
		{
			Write($"f{i}.txt", "x");
		}

		var sent = 0;
		await _session.HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(Repo)), new EnvelopeContext("conn-1", null, (_, _) =>
		{
			sent++;
			_session.Dispose();
			return Task.CompletedTask;
		}), Ct);

		await _session.MirrorChangedAsync(null, Ct);

		Assert.Equal(1, sent);
	}

	[Fact]
	public async Task LargeMirrorFile_NotPushed_LoggedOnce()
	{
		using (var stream = File.Create(PathOf("big.bin")))
		{
			stream.SetLength(SyncLimits.MaxFileSize + 1);
		}

		Write("small.txt", "s");
		_projects.SetBaselined(Repo);
		await OpenAsync();

		await _session.MirrorChangedAsync(null, Ct);
		await _session.MirrorChangedAsync(["big.bin"], Ct);

		Assert.Equal(["small.txt"], Pushes.Select(c => c.Path));
		Assert.Single(_logger.Messages, m => m == $"Not sending big.bin ({SyncLimits.MaxFileSize + 1} bytes) to the browser: larger than the sync limit");
	}

	[Fact]
	public async Task ManyChanges_PushedInPages()
	{
		_projects.SetBaselined(Repo);
		for (var i = 0; i < SyncLimits.MaxPageEntries + 1; i++)
		{
			Write($"f{i}.txt", "x");
		}

		await OpenAsync();

		await _session.MirrorChangedAsync(null, Ct);

		var pages = _pushed.Select(e => Read<SyncRemotePayload>(e).Changes).ToList();
		Assert.True(pages.Count > 1);
		Assert.Equal(SyncLimits.MaxPageEntries + 1, pages.Sum(p => p.Count));
		Assert.All(pages, p => Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(p, JsonSerializerOptions.Web).Length, 1, SyncLimits.MaxPageBytes));
	}

	private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private static ManifestEntry Entry(string path, string content) => new(path, Encoding.UTF8.GetByteCount(content), Sha(content));

	private static Envelope Manifest(bool final, params ManifestEntry[] entries) =>
		Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(Repo, entries, final));

	private static Envelope Delta(IEnumerable<ManifestEntry> upserts) =>
		Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [.. upserts], []));

	private static Envelope Chunk(string path, string content) =>
		Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, path, 0, SyncData.Encode(Encoding.UTF8.GetBytes(content)), true));

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private string PathOf(string path) => Path.Combine(_repoRoot, path);

	private void Write(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(PathOf(path))!);
		File.WriteAllText(PathOf(path), content);
	}

	/// <summary>A repo that had its first full manifest, with these agreed contents.</summary>
	private void Baseline(params (string Path, string Content)[] bases)
	{
		_projects.SetBaselined(Repo);
		_projects.SetBases(Repo, [.. bases.Select(b => new KeyValuePair<string, string?>(b.Path, Sha(b.Content)))]);
	}

	private Task<Envelope?> HandleAsync(Envelope request) => _session.HandleAsync(request, Context, Ct);

	private async Task OpenAsync() => await HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(Repo)));

	private async Task<IReadOnlyList<string>> NeedAsync(Envelope request)
	{
		var reply = await HandleAsync(request);
		Assert.Equal(MessageTypes.SyncNeed, reply!.Type);
		return Read<SyncNeedPayload>(reply).Paths;
	}

	private async Task AssertError(string code, Envelope request)
	{
		var ex = await Assert.ThrowsAsync<EnvelopeException>(() => HandleAsync(request));
		Assert.Equal(code, ex.Code);
	}
}
