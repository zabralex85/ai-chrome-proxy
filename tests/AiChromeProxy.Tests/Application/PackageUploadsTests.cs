using System.Security.Cryptography;
using System.Text.Json;
using AiChromeProxy.Application.Packages;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Packages;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PkgFile = (string Path, byte[] Data);

namespace AiChromeProxy.Tests.Application;

public sealed class PackageUploadsTests : IDisposable
{
	private const string Conn = "c1";
	private const string Nupkg = "corp.a.1.2.3.nupkg";
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FileSystemPackageStore _store;
	private readonly ListLogger<PackageUploads> _logger = new();
	private readonly PackageUploads _uploads;

	public PackageUploadsTests()
	{
		_store = new FileSystemPackageStore(Options.Create(new PackagesOptions { Root = _root }));
		_uploads = new PackageUploads(_store, _logger);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Have_ListsOnlyPackagesNotOnTheServer()
	{
		var nupkg = new byte[] { 1 };
		Store(nupkg);

		var missing = _uploads.Have(new NuGetHavePayload([new("Corp.A", "1.2.3", Hash(nupkg)), new("corp.b", "2.0.0", Hash(nupkg))]));

		Assert.Equal([new NuGetPackageKey("corp.b", "2.0.0")], missing.Missing);
	}

	[Theory]
	[InlineData("../x", "1.0.0", true)]
	[InlineData("corp.a", "1/0", true)]
	[InlineData("corp.a", "1.0.0", false)]
	public void Have_InvalidIdOrVersionOrHash_IsBadRequest(string id, string version, bool validHash)
	{
		var hash = validHash ? Hash([1]) : "nope";

		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _uploads.Have(new NuGetHavePayload([new(id, version, hash)]))).Code);
	}

	[Fact]
	public void Have_MoreThan2000_IsTooLarge()
	{
		var many = Enumerable.Range(0, 2001).Select(i => new NuGetPackageInfo("p" + i, "1.0.0", Hash([1]))).ToList();

		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _uploads.Have(new NuGetHavePayload(many))).Code);
	}

	[Fact]
	public void Have_NullEntry_IsBadRequest()
	{
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _uploads.Have(new NuGetHavePayload([null!]))).Code);
	}

	[Fact]
	public void Upload_StoresThePackage_AndAnswersTheLastChunkOnly()
	{
		var nupkg = Bytes(40_000);
		var dll = new byte[] { 9, 8, 7 };
		PkgFile[] files = [(Nupkg, nupkg), ("empty.txt", []), ("lib/net10.0/a.dll", dll)];
		var begun = _uploads.Begin(Conn, Begin(nupkg, files));

		var replies = SendAll(Conn, begun, files);

		Assert.All(replies.SkipLast(1), Assert.Null);
		Assert.Equal(new NuGetStoredPayload("corp.a", "1.2.3"), replies[^1]);
		Assert.True(_store.HasNuGet("corp.a", "1.2.3"));
		var folder = Path.Combine(_root, "nuget", "corp.a", "1.2.3");
		Assert.Equal(nupkg, File.ReadAllBytes(Path.Combine(folder, Nupkg)));
		Assert.Empty(File.ReadAllBytes(Path.Combine(folder, "empty.txt")));
		Assert.Equal(dll, File.ReadAllBytes(Path.Combine(folder, "lib", "net10.0", "a.dll")));
		Assert.Empty(Temps());
	}

	[Fact]
	public void Upload_HashMismatch_FailsOnTheLastChunk_WithTheChangedMessage_AndLeavesNothing()
	{
		var nupkg = new byte[] { 1, 2, 3 };
		PkgFile[] files = [(Nupkg, nupkg)];
		var begun = _uploads.Begin(Conn, Begin(nupkg, files) with { ContentHash = Hash([4]) });

		var error = Assert.Throws<EnvelopeException>(() => SendAll(Conn, begun, files));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
		Assert.Equal(PackageUploads.ChangedMessage, error.Message);
		Assert.False(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Empty(Temps());
	}

	[Fact]
	public void Begin_Limits()
	{
		var ok = Begin([1], [(Nupkg, [1])]);

		Assert.Equal(ErrorCodes.TooLarge, Code(ok with { FileCount = PackageLimits.MaxPackageFiles + 1 }));
		Assert.Equal(ErrorCodes.TooLarge, Code(ok with { TotalSize = PackageLimits.MaxPackageBytes + 1 }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { FileCount = 0 }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { TotalSize = -1 }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { Id = "a/b" }));
		Assert.Empty(Temps());

		string Code(NuGetBeginPayload p) => Assert.Throws<EnvelopeException>(() => _uploads.Begin(Conn, p)).Code;
	}

	[Fact]
	public void Begin_AtTheLimits_IsAccepted()
	{
		var begun = _uploads.Begin(Conn, Begin([1], [(Nupkg, [1])]) with { FileCount = PackageLimits.MaxPackageFiles, TotalSize = PackageLimits.MaxPackageBytes });

		Assert.NotEmpty(begun.UploadId);
	}

	[Fact]
	public void FirstChunk_OfAFile_IsChecked()
	{
		var nupkg = new byte[] { 1 };
		var data = SyncData.Encode(nupkg);

		Assert.Equal(ErrorCodes.BadRequest, Last(new NuGetChunkPayload("", "../x", 0, string.Empty, true, 0)));
		Assert.Equal(ErrorCodes.BadRequest, Last(new NuGetChunkPayload("", Nupkg, 0, data, true)));
		Assert.Equal(ErrorCodes.BadRequest, Last(new NuGetChunkPayload("", Nupkg, 1, data, true, 1)));
		Assert.Equal(ErrorCodes.BadRequest, Last(new NuGetChunkPayload("", Nupkg, 0, string.Empty, true, -1)));
		Assert.Equal(ErrorCodes.TooLarge, Last(new NuGetChunkPayload("", Nupkg, 0, data, true, 2)));
		Assert.Equal(ErrorCodes.TooLarge, Last(new NuGetChunkPayload("", Nupkg, 0, data, true, long.MaxValue)));
		Assert.Empty(Temps());

		string Last(NuGetChunkPayload chunk)
		{
			var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg)]));
			return Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, chunk with { UploadId = begun.UploadId })).Code;
		}
	}

	[Fact]
	public void FirstChunk_RepeatedPathIgnoringCase_IsBadRequest()
	{
		var nupkg = new byte[] { 1 };
		var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg), ("A.txt", [1]), ("a.TXT", [1])]));
		_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode(nupkg), false, 1));
		_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "A.txt", 0, SyncData.Encode([1]), false, 1));

		var error = Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "a.TXT", 0, SyncData.Encode([1]), true, 1)));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
		Assert.Contains("repeated", error.Message);
	}

	[Fact]
	public void FirstChunk_OfMoreFilesThanAnnounced_IsBadRequest()
	{
		var nupkg = new byte[] { 1 };
		var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg)]) with { TotalSize = 5 });
		_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode(nupkg), false, 1));

		var error = Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 0, string.Empty, true, 0)));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
		Assert.Contains("More files", error.Message);
	}

	[Fact]
	public void LaterChunk_WithASize_IsOutOfOrder()
	{
		var nupkg = new byte[] { 1, 2 };
		var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg)]));
		_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode([1]), false, 2));

		var error = Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 1, SyncData.Encode([2]), true, 2)));

		Assert.Contains("out of order", error.Message);
	}

	[Theory]
	[InlineData(2, 1, Nupkg)]
	[InlineData(1, 5, Nupkg)]
	[InlineData(1, 1, "other.txt")]
	[InlineData(1, 1, "Corp.A.1.2.3.nupkg")]
	public void LastChunk_NeedsEveryAnnouncedFile_TheTotalSize_AndTheLowerCaseNupkg(int fileCount, long totalSize, string path)
	{
		var nupkg = new byte[] { 1 };
		var begun = _uploads.Begin(Conn, Begin(nupkg, [(path, nupkg)]) with { FileCount = fileCount, TotalSize = totalSize });

		var error = Assert.Throws<EnvelopeException>(() => SendAll(Conn, begun, [(path, nupkg)]));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
		Assert.False(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Empty(Temps());
	}

	[Fact]
	public void Chunk_OutOfOrder_DropsTheRest_AndTheLastGetsTheFirstError()
	{
		var nupkg = new byte[] { 1, 2 };
		var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg)]));

		Assert.Null(_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 3, SyncData.Encode([1, 2]), false, 2)));
		var error = Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode([1, 2]), true, 2)));

		Assert.Contains("'x'", error.Message);
		Assert.Contains("out of order", error.Message);
		Assert.Empty(Temps());
	}

	[Fact]
	public void Chunk_WrongPath_BadData_TooMuch_AndIncomplete_Fail()
	{
		var nupkg = new byte[] { 1, 2 };

		Assert.Equal(ErrorCodes.BadRequest, Last("x", string.Empty));
		Assert.Equal(ErrorCodes.BadRequest, Last(Nupkg, "a+b/"));
		Assert.Equal(ErrorCodes.TooLarge, Last(Nupkg, SyncData.Encode([1, 2, 3])));
		Assert.Equal(ErrorCodes.BadRequest, Last(Nupkg, SyncData.Encode([1])));

		string Last(string path, string data)
		{
			var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg)]));
			return Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, path, 0, data, true, 2))).Code;
		}
	}

	[Fact]
	public void Chunk_OverTheChunkSize_IsTooLarge()
	{
		var nupkg = new byte[PackageLimits.ChunkSize + 1];
		var begun = _uploads.Begin(Conn, Begin(nupkg, [(Nupkg, nupkg)]));

		var error = Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode(nupkg), true, nupkg.Length)));

		Assert.Equal(ErrorCodes.TooLarge, error.Code);
	}

	[Fact]
	public void Chunk_UnknownUpload_LastIsNotFound()
	{
		Assert.Null(_uploads.Chunk(Conn, new NuGetChunkPayload("x", "a", 0, string.Empty, false, 0)));
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload("x", "a", 0, string.Empty, true, 0))).Code);
	}

	[Fact]
	public void Begin_Again_DiscardsTheConnectionsPreviousUpload()
	{
		var begin = Begin([1], [(Nupkg, [1])]);
		_uploads.Begin(Conn, begin);
		_uploads.Begin(Conn, begin);

		Assert.Single(Temps());
	}

	[Fact]
	public void Close_DiscardsTheUpload()
	{
		var begun = _uploads.Begin(Conn, Begin([1], [(Nupkg, [1])]));

		_uploads.Close(Conn);
		_uploads.Close(Conn);

		Assert.Empty(Temps());
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 0, string.Empty, true, 0))).Code);
	}

	[Fact]
	public void Upload_OfAPackageAlreadyThere_StillAnswersStored()
	{
		var nupkg = new byte[] { 1 };
		PkgFile[] files = [(Nupkg, nupkg)];
		var begun = _uploads.Begin(Conn, Begin(nupkg, files));
		Store(nupkg);

		var replies = SendAll(Conn, begun, files);

		Assert.Equal(new NuGetStoredPayload("corp.a", "1.2.3"), replies[^1]);
		Assert.Empty(Temps());
	}

	[Fact]
	public void Upload_StoreFailure_IsInternal_AndLogged()
	{
		var nupkg = new byte[] { 1 };
		PkgFile[] files = [(Nupkg, nupkg)];
		var begun = _uploads.Begin(Conn, Begin(nupkg, files));
		// A folder where the file belongs makes the append fail with an IO error.
		Directory.CreateDirectory(Path.Combine(Temps().Single(), Nupkg));

		var error = Assert.Throws<EnvelopeException>(() => SendAll(Conn, begun, files));

		Assert.Equal(ErrorCodes.Internal, error.Code);
		Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
	}

	[Fact]
	public void Constructor_DeletesLeftoverTemps()
	{
		_store.Begin("corp.a", "1.0.0", "abcd1234");
		var logger = new ListLogger<PackageUploads>();

		_ = new PackageUploads(_store, logger);

		Assert.Empty(Temps());
		Assert.Contains(logger.Messages, m => m.Contains("Deleted 1 "));
	}

	[Fact]
	public void Constructor_SurvivesAFailingCleanup()
	{
		var logger = new ListLogger<PackageUploads>();

		_ = new PackageUploads(new ThrowingStore(), logger);

		Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
	}

	[Fact]
	public async Task Handler_RoutesTypes_AndRepliesWithCorrelationId()
	{
		var nupkg = new byte[] { 1, 2 };
		var router = new EnvelopeRouter([.. PackagesHandler.Types.Select(t => new PackagesHandler(t, _uploads))]);
		var context = new EnvelopeContext(Conn, "u@example.com", (_, _) => Task.CompletedTask);
		var ct = TestContext.Current.CancellationToken;
		var begin = Begin(nupkg, [(Nupkg, nupkg)]);

		var missing = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetHave, new NuGetHavePayload([new(begin.Id, begin.Version, begin.ContentHash)]), "h"), context, ct);
		var begun = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetBegin, begin, "b"), context, ct);
		var uploadId = begun!.Payload.Deserialize<NuGetBegunPayload>(JsonSerializerOptions.Web)!.UploadId;
		var first = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetChunk, new NuGetChunkPayload(uploadId, Nupkg, 0, SyncData.Encode(nupkg[..1]), false, 2), "c1"), context, ct);
		var last = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetChunk, new NuGetChunkPayload(uploadId, Nupkg, 1, SyncData.Encode(nupkg[1..]), true), "c2"), context, ct);
		var bad = await router.RouteAsync(new Envelope(MessageTypes.PackagesNuGetHave, JsonDocument.Parse("[]").RootElement, "x"), context, ct);

		Assert.Equal((MessageTypes.PackagesNuGetMissing, "h"), (missing!.Type, missing.CorrelationId));
		Assert.Equal((MessageTypes.PackagesNuGetBegun, "b"), (begun.Type, begun.CorrelationId));
		Assert.Null(first);
		Assert.Equal((MessageTypes.PackagesNuGetStored, "c2"), (last!.Type, last.CorrelationId));
		Assert.Equal(MessageTypes.Error, bad!.Type);
	}

	[Fact]
	public void Close_And_FailedUpload_SurviveADiscardThatFails()
	{
		var uploads = new PackageUploads(new DiscardFailingStore(_store), _logger);
		var begin = Begin([1, 2], [(Nupkg, [1, 2])]);
		var begun = uploads.Begin(Conn, begin);
		uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 1, string.Empty, false, 0));

		var error = Assert.Throws<EnvelopeException>(() => uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode([1, 2]), true, 2)));
		Assert.Contains("'x'", error.Message);

		uploads.Begin(Conn, begin);
		uploads.Close(Conn);
		Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
	}

	[Fact]
	public void Chunk_AfterClose_IsUnknown_AndDoesNotRecreateTheTemp()
	{
		var begun = _uploads.Begin(Conn, Begin([1], [(Nupkg, [1])]));
		_uploads.Close(Conn);

		Assert.Null(_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode([1]), false, 1)));
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, Nupkg, 0, SyncData.Encode([1]), true, 1))).Code);
		Assert.Empty(Temps());
	}

	private static string Hash(byte[] data) => Convert.ToBase64String(SHA512.HashData(data));

	private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

	/// <summary>The begin of corp.a 1.2.3 whose <c>.nupkg</c> is <paramref name="nupkg"/>, announcing <paramref name="files"/> (count and total size).</summary>
	private static NuGetBeginPayload Begin(byte[] nupkg, PkgFile[] files) => new("corp.a", "1.2.3", Hash(nupkg), files.Length, files.Sum(f => (long)f.Data.Length));

	private string[] Temps() => Directory.Exists(Path.Combine(_root, "nuget"))
		? Directory.GetDirectories(Path.Combine(_root, "nuget"), "~*", SearchOption.AllDirectories)
		: [];

	private void Store(byte[] nupkg)
	{
		var handle = _store.Begin("corp.a", "1.2.3", "seed0000");
		_store.Append(handle, Nupkg, nupkg, create: true);
		_store.Commit(handle, "corp.a", "1.2.3");
	}

	/// <summary>Sends the files in 16 KB chunks like the browser: each file's size with its first chunk, a size-0 file as one empty chunk.</summary>
	private List<NuGetStoredPayload?> SendAll(string conn, NuGetBegunPayload begun, PkgFile[] files)
	{
		var replies = new List<NuGetStoredPayload?>();
		for (var f = 0; f < files.Length; f++)
		{
			var data = files[f].Data;
			var offset = 0;
			do
			{
				var size = Math.Min(PackageLimits.ChunkSize, data.Length - offset);
				var last = f == files.Length - 1 && offset + size == data.Length;
				long? fileSize = offset == 0 ? data.Length : null;
				replies.Add(_uploads.Chunk(conn, new NuGetChunkPayload(begun.UploadId, files[f].Path, offset, SyncData.Encode(data.AsSpan(offset, size)), last, fileSize)));
				offset += size;
			}
			while (offset < data.Length);
		}

		return replies;
	}

	private sealed class ThrowingStore : IPackageStore
	{
		public string NuGetFolder => "";

		public bool HasNuGet(string id, string version) => false;

		public string Begin(string id, string version, string tag) => "";

		public void Append(string upload, string path, byte[] data, bool create)
		{
		}

		public string Sha512(string upload, string path) => "";

		public bool Commit(string upload, string id, string version) => true;

		public void Discard(string upload)
		{
		}

		public int DeleteTemps() => throw new IOException("locked");
	}

	private sealed class DiscardFailingStore(IPackageStore inner) : IPackageStore
	{
		public string NuGetFolder => inner.NuGetFolder;

		public bool HasNuGet(string id, string version) => inner.HasNuGet(id, version);

		public string Begin(string id, string version, string tag) => inner.Begin(id, version, tag);

		public void Append(string upload, string path, byte[] data, bool create) => inner.Append(upload, path, data, create);

		public string Sha512(string upload, string path) => inner.Sha512(upload, path);

		public bool Commit(string upload, string id, string version) => inner.Commit(upload, id, version);

		public void Discard(string upload) => throw new IOException("locked");

		public int DeleteTemps() => 0;
	}
}
