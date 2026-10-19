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

namespace AiChromeProxy.Tests.Application;

public sealed class PackageUploadsTests : IDisposable
{
	private const string Conn = "c1";
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
	public void Upload_StoresThePackage_AndAnswersTheLastChunkOnly()
	{
		var nupkg = Bytes(40_000);
		var dll = new byte[] { 9, 8, 7 };
		var begin = Pkg(nupkg, ("empty.txt", []), ("lib/net10.0/a.dll", dll));
		var begun = _uploads.Begin(Conn, begin);

		var replies = SendAll(Conn, begun, begin, [nupkg, [], dll]);

		Assert.All(replies.SkipLast(1), Assert.Null);
		Assert.Equal(new NuGetStoredPayload("corp.a", "1.2.3"), replies[^1]);
		Assert.True(_store.HasNuGet("corp.a", "1.2.3"));
		var folder = Path.Combine(_root, "nuget", "corp.a", "1.2.3");
		Assert.Equal(nupkg, File.ReadAllBytes(Path.Combine(folder, "corp.a.1.2.3.nupkg")));
		Assert.Empty(File.ReadAllBytes(Path.Combine(folder, "empty.txt")));
		Assert.Equal(dll, File.ReadAllBytes(Path.Combine(folder, "lib", "net10.0", "a.dll")));
		Assert.Empty(Temps());
	}

	[Fact]
	public void Upload_HashMismatch_FailsOnTheLastChunk_WithTheChangedMessage_AndLeavesNothing()
	{
		var nupkg = new byte[] { 1, 2, 3 };
		var begin = Pkg(nupkg) with { ContentHash = Hash([4]) };
		var begun = _uploads.Begin(Conn, begin);

		var error = Assert.Throws<EnvelopeException>(() => SendAll(Conn, begun, begin, [nupkg]));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
		Assert.Equal(PackageUploads.ChangedMessage, error.Message);
		Assert.False(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Empty(Temps());
	}

	[Fact]
	public void Begin_Limits()
	{
		var ok = Pkg(new byte[] { 1 });
		var many = ok.Files.Concat(Enumerable.Range(0, 2000).Select(i => new NuGetFileInfo($"f{i}.txt", 1))).ToList();

		Assert.Equal(ErrorCodes.TooLarge, Code(ok with { Files = many }));
		Assert.Equal(ErrorCodes.TooLarge, Code(ok with { Files = [.. ok.Files, new NuGetFileInfo("big.bin", PackageLimits.MaxPackageBytes)] }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { Files = [.. ok.Files, new NuGetFileInfo("../x", 1)] }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { Files = [.. ok.Files, new NuGetFileInfo("A.txt", 1), new NuGetFileInfo("a.TXT", 1)] }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { Files = [new NuGetFileInfo("other.txt", 1)] }));
		Assert.Equal(ErrorCodes.BadRequest, Code(ok with { Id = "a/b" }));

		string Code(NuGetBeginPayload p) => Assert.Throws<EnvelopeException>(() => _uploads.Begin(Conn, p)).Code;
	}

	[Fact]
	public void Chunk_OutOfOrder_DropsTheRest_AndTheLastGetsTheFirstError()
	{
		var begin = Pkg(new byte[] { 1, 2 });
		var begun = _uploads.Begin(Conn, begin);
		var path = begin.Files[0].Path;

		Assert.Null(_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 0, SyncData.Encode([1, 2]), false)));
		var error = Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, path, 0, SyncData.Encode([1, 2]), true)));

		Assert.Contains("'x'", error.Message);
		Assert.Contains("out of order", error.Message);
		Assert.Empty(Temps());
	}

	[Fact]
	public void Chunk_WrongPath_BadData_TooMuch_AndIncomplete_Fail()
	{
		var begin = Pkg(new byte[] { 1, 2 });
		var path = begin.Files[0].Path;

		Assert.Equal(ErrorCodes.BadRequest, Last(path: "x", data: ""));
		Assert.Equal(ErrorCodes.BadRequest, Last(path, "a+b/"));
		Assert.Equal(ErrorCodes.TooLarge, Last(path, SyncData.Encode([1, 2, 3])));
		Assert.Equal(ErrorCodes.BadRequest, Last(path, SyncData.Encode([1])));

		string Last(string path, string data)
		{
			var begun = _uploads.Begin(Conn, begin);
			return Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, path, 0, data, true))).Code;
		}
	}

	[Fact]
	public void Chunk_MoreThanTheFiles_Fails()
	{
		var nupkg = new byte[] { 1 };
		var begin = Pkg(nupkg);
		var begun = _uploads.Begin(Conn, begin);
		_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, begin.Files[0].Path, 0, SyncData.Encode(nupkg), false));

		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 0, "", true))).Code);
	}

	[Fact]
	public void Chunk_UnknownUpload_LastIsNotFound()
	{
		Assert.Null(_uploads.Chunk(Conn, new NuGetChunkPayload("x", "a", 0, "", false)));
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload("x", "a", 0, "", true))).Code);
	}

	[Fact]
	public void Begin_Again_DiscardsTheConnectionsPreviousUpload()
	{
		var begin = Pkg(new byte[] { 1 });
		_uploads.Begin(Conn, begin);
		_uploads.Begin(Conn, begin);

		Assert.Single(Temps());
	}

	[Fact]
	public void Close_DiscardsTheUpload()
	{
		var begun = _uploads.Begin(Conn, Pkg(new byte[] { 1 }));

		_uploads.Close(Conn);
		_uploads.Close(Conn);

		Assert.Empty(Temps());
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 0, "", true))).Code);
	}

	[Fact]
	public void Upload_OfAPackageAlreadyThere_StillAnswersStored()
	{
		var nupkg = new byte[] { 1 };
		var begin = Pkg(nupkg);
		var begun = _uploads.Begin(Conn, begin);
		Store(nupkg);

		var replies = SendAll(Conn, begun, begin, [nupkg]);

		Assert.Equal(new NuGetStoredPayload("corp.a", "1.2.3"), replies[^1]);
		Assert.Empty(Temps());
	}

	[Fact]
	public void Upload_StoreFailure_IsInternal_AndLogged()
	{
		var nupkg = new byte[] { 1 };
		var begin = Pkg(nupkg);
		var begun = _uploads.Begin(Conn, begin);
		// A folder where the file belongs makes the append fail with an IO error.
		Directory.CreateDirectory(Path.Combine(Temps().Single(), begin.Files[0].Path));

		var error = Assert.Throws<EnvelopeException>(() => SendAll(Conn, begun, begin, [nupkg]));

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
		var begin = Pkg(nupkg);
		var path = begin.Files[0].Path;

		var missing = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetHave, new NuGetHavePayload([new(begin.Id, begin.Version, begin.ContentHash)]), "h"), context, ct);
		var begun = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetBegin, begin, "b"), context, ct);
		var uploadId = begun!.Payload.Deserialize<NuGetBegunPayload>(JsonSerializerOptions.Web)!.UploadId;
		var first = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetChunk, new NuGetChunkPayload(uploadId, path, 0, SyncData.Encode(nupkg[..1]), false), "c1"), context, ct);
		var last = await router.RouteAsync(Envelope.Create(MessageTypes.PackagesNuGetChunk, new NuGetChunkPayload(uploadId, path, 1, SyncData.Encode(nupkg[1..]), true), "c2"), context, ct);
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
		var begin = Pkg(new byte[] { 1, 2 });
		var begun = uploads.Begin(Conn, begin);
		uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, "x", 0, "", false));

		var error = Assert.Throws<EnvelopeException>(() => uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, begin.Files[0].Path, 0, SyncData.Encode([1, 2]), true)));
		Assert.Contains("'x'", error.Message);

		uploads.Begin(Conn, begin);
		uploads.Close(Conn);
		Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
	}

	[Fact]
	public void Begin_FileSizesThatWouldOverflowTheSum_AreTooLarge()
	{
		var ok = Pkg(new byte[] { 1 });

		var error = Assert.Throws<EnvelopeException>(() => _uploads.Begin(Conn, ok with { Files = [.. ok.Files, new NuGetFileInfo("a", long.MaxValue / 2), new NuGetFileInfo("b", long.MaxValue / 2)] }));

		Assert.Equal(ErrorCodes.TooLarge, error.Code);
	}

	[Fact]
	public void NullEntries_AreBadRequests()
	{
		var ok = Pkg(new byte[] { 1 });

		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _uploads.Have(new NuGetHavePayload([null!]))).Code);
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _uploads.Begin(Conn, ok with { Files = [null!] })).Code);
	}

	[Fact]
	public void Chunk_AfterClose_IsUnknown_AndDoesNotRecreateTheTemp()
	{
		var begin = Pkg(new byte[] { 1 });
		var begun = _uploads.Begin(Conn, begin);
		_uploads.Close(Conn);

		Assert.Null(_uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, begin.Files[0].Path, 0, SyncData.Encode([1]), false)));
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _uploads.Chunk(Conn, new NuGetChunkPayload(begun.UploadId, begin.Files[0].Path, 0, SyncData.Encode([1]), true))).Code);
		Assert.Empty(Temps());
	}

	[Fact]
	public void Begin_NupkgNameMustBeLowerCase()
	{
		var ok = Pkg(new byte[] { 1 });

		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _uploads.Begin(Conn, ok with { Files = [new NuGetFileInfo("Corp.A.1.2.3.nupkg", 1)] })).Code);
	}

	private static string Hash(byte[] data) => Convert.ToBase64String(SHA512.HashData(data));

	private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

	private static NuGetBeginPayload Pkg(byte[] nupkg, params (string Path, byte[] Data)[] others) =>
		new("corp.a", "1.2.3", Hash(nupkg), [new("corp.a.1.2.3.nupkg", nupkg.Length), .. others.Select(o => new NuGetFileInfo(o.Path, o.Data.Length))]);

	private string[] Temps() => Directory.Exists(Path.Combine(_root, "nuget"))
		? Directory.GetDirectories(Path.Combine(_root, "nuget"), "~*", SearchOption.AllDirectories)
		: [];

	private void Store(byte[] nupkg)
	{
		var handle = _store.Begin("corp.a", "1.2.3", "seed0000");
		_store.Append(handle, "corp.a.1.2.3.nupkg", nupkg, create: true);
		_store.Commit(handle, "corp.a", "1.2.3");
	}

	private List<NuGetStoredPayload?> SendAll(string conn, NuGetBegunPayload begun, NuGetBeginPayload begin, byte[][] contents)
	{
		var replies = new List<NuGetStoredPayload?>();
		for (var f = 0; f < begin.Files.Count; f++)
		{
			var data = contents[f];
			var offset = 0;
			do
			{
				var size = Math.Min(PackageLimits.ChunkSize, data.Length - offset);
				var last = f == begin.Files.Count - 1 && offset + size == data.Length;
				replies.Add(_uploads.Chunk(conn, new NuGetChunkPayload(begun.UploadId, begin.Files[f].Path, offset, SyncData.Encode(data.AsSpan(offset, size)), last)));
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
