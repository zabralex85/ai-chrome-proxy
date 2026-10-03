using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Application;

/// <summary>The server side of the protocol against a real mirror in a temp folder.</summary>
public sealed class SyncSessionTests : IDisposable
{
	private const string Repo = "repo";

	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _repoRoot;
	private readonly ListLogger<SyncSession> _logger = new();
	private readonly SyncSession _session;

	public SyncSessionTests()
	{
		_repoRoot = Path.Combine(_root, Repo);
		Directory.CreateDirectory(_repoRoot);
		_session = new SyncSession(new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root })), _logger, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose()
	{
		_session.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public async Task Open_SanitizesRepo_RepliesOpenedWithCorrelationId()
	{
		var reply = await _session.HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("My Repo"), "c1"), Ct);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.SyncOpened, reply.Type);
		Assert.Equal("c1", reply.CorrelationId);
		Assert.Equal("My_Repo", Read<SyncOpenPayload>(reply).Repo);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("  ")]
	public async Task Open_NoName_BadRequest(string? name)
	{
		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(name!)));
	}

	[Fact]
	public async Task ManifestBeforeOpen_Or_OtherRepo_BadRequest()
	{
		await AssertError(ErrorCodes.BadRequest, Manifest(final: true));
		await OpenAsync();
		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload("other", [], true)));
	}

	[Theory]
	[InlineData("null")]
	[InlineData("\"text\"")]
	[InlineData("{\"repo\":\"repo\",\"entries\":\"x\"}")]
	public async Task MalformedPayload_BadRequest(string json)
	{
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, new Envelope(MessageTypes.SyncManifest, JsonDocument.Parse(json).RootElement.Clone()));
	}

	[Theory]
	[InlineData(MessageTypes.SyncOpen, "{}")]
	[InlineData(MessageTypes.SyncManifest, "{\"repo\":\"repo\",\"final\":true}")]
	[InlineData(MessageTypes.SyncManifest, "{\"repo\":\"repo\",\"entries\":[null],\"final\":true}")]
	[InlineData(MessageTypes.SyncManifest, "{\"repo\":\"repo\",\"entries\":[{\"path\":\"a.txt\",\"size\":1}],\"final\":true}")]
	[InlineData(MessageTypes.SyncDelta, "{\"repo\":\"repo\",\"deletes\":[]}")]
	[InlineData(MessageTypes.SyncDelta, "{\"repo\":\"repo\",\"upserts\":[]}")]
	[InlineData(MessageTypes.SyncDelta, "{\"repo\":\"repo\",\"upserts\":[],\"deletes\":[null]}")]
	[InlineData(MessageTypes.SyncChunk, "{\"repo\":\"repo\",\"path\":\"a.txt\",\"offset\":0,\"last\":true}")]
	[InlineData(MessageTypes.SyncChunk, "{\"repo\":\"repo\",\"offset\":0,\"data\":\"\",\"last\":true}")]
	[InlineData(MessageTypes.SyncChunk, "{\"path\":\"a.txt\",\"offset\":0,\"data\":\"\",\"last\":true}")]
	public async Task MissingField_BadRequest(string type, string json)
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "a")));

		await AssertError(ErrorCodes.BadRequest, new Envelope(type, JsonDocument.Parse(json).RootElement.Clone()));
	}

	[Fact]
	public async Task Manifest_NeedsMissingAndChanged_NotUnchanged()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "same.txt"), "same");
		File.WriteAllText(Path.Combine(_repoRoot, "changed.txt"), "old");
		await OpenAsync();

		var need = await NeedAsync(Manifest(true, Entry("same.txt", "same"), Entry("changed.txt", "new"), Entry("new/file.txt", "x")));

		Assert.Equal(["changed.txt", "new/file.txt"], need);
	}

	[Fact]
	public async Task FinalPage_DeletesFilesNotInAnyPage_IgnoringCase()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "old"));
		File.WriteAllText(Path.Combine(_repoRoot, "old", "gone.txt"), "g");
		File.WriteAllText(Path.Combine(_repoRoot, "page1.txt"), "1");
		File.WriteAllText(Path.Combine(_repoRoot, "Readme.md"), "r");
		await OpenAsync();

		Assert.Empty(await NeedAsync(Manifest(false, Entry("page1.txt", "1"))));
		Assert.Empty(await NeedAsync(Manifest(true, Entry("README.md", "r"))));

		Assert.False(Directory.Exists(Path.Combine(_repoRoot, "old")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "page1.txt")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "Readme.md")));
		Assert.Contains(_logger.Messages, m => m.Contains("Sync repo: manifest of 2 files, 0 to upload, 1 deleted", StringComparison.Ordinal));
	}

	[Fact]
	public async Task FinalPage_DeletesStaleTempFiles_KeepsOpenUpload()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
		File.WriteAllText(Path.Combine(_repoRoot, "src", "old.cs.aicp-tmp"), "crashed upload");
		await OpenAsync();
		await NeedAsync(Manifest(false, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);

		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));

		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "old.cs.aicp-tmp")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
		Assert.Equal(MessageTypes.SyncStored, (await _session.HandleAsync(Chunk("a.txt", 2, "cd"u8.ToArray(), last: true), Ct))!.Type);
		Assert.Equal("abcd", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
	}

	[Theory]
	[InlineData("../escape.txt", 1, ErrorCodes.BadRequest)]
	[InlineData("ok.txt", -1, ErrorCodes.BadRequest)]
	[InlineData("ok.txt", SyncLimits.MaxFileSize + 1, ErrorCodes.TooLarge)]
	public async Task Manifest_InvalidEntry_Refused(string path, long size, string code)
	{
		await OpenAsync();

		await AssertError(code, Manifest(true, new ManifestEntry(path, size, Sha("x"))));
	}

	[Theory]
	[InlineData("")]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("abc")]
	public async Task Manifest_BadHash_BadRequest(string sha)
	{
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Manifest(true, new ManifestEntry("a.txt", 1, sha)));
	}

	[Fact]
	public async Task Manifest_MoreThanMaxFiles_TooLarge()
	{
		await OpenAsync();
		var entries = Enumerable.Range(0, SyncLimits.MaxFiles + 1).Select(i => new ManifestEntry($"f{i}", 0, Sha(string.Empty))).ToArray();

		await AssertError(ErrorCodes.TooLarge, Manifest(true, entries));
	}

	[Fact]
	public async Task Chunks_InOrder_FileStoredWithHash_StoredReplyOnLastOnly()
	{
		var content = RandomNumberGenerator.GetBytes((SyncLimits.ChunkSize * 2) + 100);
		await OpenAsync();
		await NeedAsync(Manifest(true, new ManifestEntry("src/big.bin", content.Length, Sha(content))));

		var replies = new List<Envelope?>();
		for (var offset = 0; offset < content.Length; offset += SyncLimits.ChunkSize)
		{
			var length = Math.Min(SyncLimits.ChunkSize, content.Length - offset);
			var last = offset + length == content.Length;
			replies.Add(await _session.HandleAsync(Chunk("src/big.bin", offset, content.AsSpan(offset, length).ToArray(), last, last ? Sha(content) : null), Ct));
		}

		Assert.Null(replies[0]);
		Assert.Null(replies[1]);
		Assert.Equal(MessageTypes.SyncStored, replies[2]!.Type);
		Assert.Equal(new SyncStoredPayload(Repo, "src/big.bin"), Read<SyncStoredPayload>(replies[2]!));
		Assert.Equal(content, File.ReadAllBytes(Path.Combine(_repoRoot, "src", "big.bin")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "big.bin.aicp-tmp")));
		Assert.Contains(_logger.Messages, m => m.StartsWith($"Sync repo: stored 1 files, {content.Length} bytes in ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task EmptyFile_OneEmptyLastChunk()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("empty.txt", string.Empty)));

		var reply = await _session.HandleAsync(Chunk("empty.txt", 0, [], last: true), Ct);

		Assert.Equal(MessageTypes.SyncStored, reply!.Type);
		Assert.Empty(File.ReadAllBytes(Path.Combine(_repoRoot, "empty.txt")));
	}

	[Fact]
	public async Task Chunk_NotNeeded_NotFound_AlreadyStored_NotFound()
	{
		await OpenAsync();
		await AssertError(ErrorCodes.NotFound, Chunk("never-asked.txt", 0, [1], last: true));

		await NeedAsync(Manifest(true, Entry("a.txt", "a")));
		await _session.HandleAsync(Chunk("a.txt", 0, "a"u8.ToArray(), last: true), Ct);

		await AssertError(ErrorCodes.NotFound, Chunk("a.txt", 0, "a"u8.ToArray(), last: true));
	}

	[Fact]
	public async Task Chunk_OutOfOrder_BadRequest_TempRemoved()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 3, "d"u8.ToArray(), last: true));

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 2, "cd"u8.ToArray(), last: true));
	}

	[Fact]
	public async Task Chunk_HashMismatch_BadRequest_OldFileKept_TempRemoved()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "old");
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "new")));

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 0, "bad"u8.ToArray(), last: true));

		Assert.Equal("old", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
	}

	[Fact]
	public async Task Chunk_ClaimedHashDiffers_BadRequest()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abc")));

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 0, "abc"u8.ToArray(), last: true, sha256: Sha("zzz")));
	}

	[Fact]
	public async Task Chunk_ShorterThanManifestSize_BadRequest()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abc")));

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 0, "ab"u8.ToArray(), last: true));
	}

	[Fact]
	public async Task Chunk_MoreBytesThanManifestSize_TooLarge_TempRemoved()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "ab")));

		await AssertError(ErrorCodes.TooLarge, Chunk("a.txt", 0, "abc"u8.ToArray(), last: false));

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
	}

	[Fact]
	public async Task Chunk_LargerThanChunkSize_TooLarge()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, new ManifestEntry("a.bin", SyncLimits.MaxFileSize, Sha("x"))));

		await AssertError(ErrorCodes.TooLarge, Chunk("a.bin", 0, new byte[SyncLimits.ChunkSize + 1], last: false));
	}

	[Fact]
	public async Task Chunk_NotBase64Url_BadRequest()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "a")));

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, "a.txt", 0, "++++", true)));
	}

	[Fact]
	public async Task Delta_UpsertsNeeded_DeletesApplied()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "keep.txt"), "k");
		File.WriteAllText(Path.Combine(_repoRoot, "gone.txt"), "g");
		await OpenAsync();

		var reply = await _session.HandleAsync(Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [Entry("keep.txt", "k"), Entry("new.txt", "n")], ["gone.txt"])), Ct);

		Assert.Equal(["new.txt"], Read<SyncNeedPayload>(reply!).Paths);
		Assert.False(File.Exists(Path.Combine(_repoRoot, "gone.txt")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "keep.txt")));
	}

	[Fact]
	public async Task Delta_InvalidDeletePath_BadRequest_NothingDeleted()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "gone.txt"), "g");
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [], ["gone.txt", "../outside.txt"])));

		Assert.True(File.Exists(Path.Combine(_repoRoot, "gone.txt")));
	}

	[Fact]
	public async Task ReopenOrDispose_DiscardsUnfinishedUpload()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));

		await OpenAsync();
		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));

		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);
		_session.Dispose();
		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
	}

	[Fact]
	public async Task NotASyncType_UnknownType()
	{
		await AssertError(ErrorCodes.UnknownType, Envelope.Create(MessageTypes.Ping, new { }));
	}

	private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private static ManifestEntry Entry(string path, string content) => new(path, Encoding.UTF8.GetByteCount(content), Sha(content));

	private static Envelope Manifest(bool final, params ManifestEntry[] entries) =>
		Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(Repo, entries, final));

	private static Envelope Chunk(string path, long offset, byte[] data, bool last, string? sha256 = null) =>
		Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, path, offset, SyncData.Encode(data), last, sha256));

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private async Task OpenAsync() => await _session.HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(Repo)), Ct);

	private async Task<IReadOnlyList<string>> NeedAsync(Envelope manifest)
	{
		var reply = await _session.HandleAsync(manifest, Ct);
		Assert.Equal(MessageTypes.SyncNeed, reply!.Type);
		return Read<SyncNeedPayload>(reply).Paths;
	}

	private async Task AssertError(string code, Envelope request)
	{
		var ex = await Assert.ThrowsAsync<EnvelopeException>(() => _session.HandleAsync(request, Ct));
		Assert.Equal(code, ex.Code);
	}
}
