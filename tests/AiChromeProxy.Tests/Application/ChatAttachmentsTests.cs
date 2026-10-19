using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Chat;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

public sealed class ChatAttachmentsTests : IDisposable
{
	private const string Id = "0123456789abcdef0123456789abcdef";
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	// Starts at the real time: the sweep compares it with the files' real write times.
	private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
	private readonly ChatAttachments _attachments;

	public ChatAttachmentsTests() =>
		_attachments = new ChatAttachments(new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = _root })), _time);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Chunks_InOrder_AnswerOnlyTheLast_AndTheFileIsResolvable()
	{
		Assert.Null(_attachments.Append(Chunk(0, [1, 2], size: 3, last: false)));
		var reply = _attachments.Append(Chunk(2, [3], size: 3, last: true));

		Assert.Equal(new ChatAttachedPayload("repo", Id), reply);
		var file = Assert.Single(_attachments.Resolve("repo", [Id]));
		Assert.Equal("shot.png", file.Name);
		Assert.Equal(3, file.Size);
		Assert.Equal([1, 2, 3], File.ReadAllBytes(file.Path));
		Assert.Equal(Path.Combine(_root, "repo"), _attachments.Folder("repo"));
	}

	[Fact]
	public void EmptyFile_IsOneLastChunk() => Assert.NotNull(_attachments.Append(Chunk(0, [], size: 0, last: true)));

	[Fact]
	public void TheNameIsSanitized()
	{
		_attachments.Append(Chunk(0, [1], size: 1, last: true, name: "..\\evil\\CON"));

		Assert.Equal("_CON", Assert.Single(_attachments.Resolve("repo", [Id])).Name);
	}

	[Theory]
	[InlineData("ABCDEF0123456789abcdef0123456789")]
	[InlineData("0123")]
	[InlineData("..")]
	[InlineData(null)]
	public void ABadId_IsABadRequest(string? id) =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], 1, true) with { Id = id! })).Code);

	[Fact]
	public void NoRepo_IsABadRequest() =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], 1, true) with { Repo = " " })).Code);

	[Fact]
	public void NotBase64Url_FailsTheUpload() =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [], 1, true) with { Data = "a+b/" })).Code);

	[Fact]
	public void OverTheSizeLimit_IsTooLargeUpFront()
	{
		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], ChatLimits.MaxAttachmentBytes + 1, true)));

		Assert.Equal(ErrorCodes.TooLarge, error.Code);
		Assert.Equal("'shot.png' is larger than 20 MB.", error.Message);
	}

	[Fact]
	public void AChunkOverSixteenKilobytes_IsTooLarge() =>
		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, new byte[ChatLimits.AttachmentChunkSize + 1], ChatLimits.AttachmentChunkSize + 1, true))).Code);

	[Fact]
	public void MoreBytesThanTheSize_IsTooLarge() =>
		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1, 2], 1, true))).Code);

	[Fact]
	public void FewerBytesThanTheSize_FailsOnTheLastChunk() =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], 2, true))).Code);

	[Fact]
	public void AFailedChunk_DropsTheRest_AndTheLastGetsTheFirstError()
	{
		_attachments.Append(Chunk(0, [1], size: 3, last: false));

		Assert.Null(_attachments.Append(Chunk(5, [2], size: 3, last: false)));
		Assert.Null(_attachments.Append(Chunk(6, [3], size: 3, last: false)));
		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(7, [4], size: 3, last: true)));

		Assert.Contains("out of order", error.Message);
		Assert.Throws<EnvelopeException>(() => _attachments.Resolve("repo", [Id]));
	}

	[Fact]
	public void OffsetZero_StartsOver_EvenAfterAFailure()
	{
		_attachments.Append(Chunk(0, [1], size: 2, last: false));
		_attachments.Append(Chunk(9, [1], size: 2, last: false));

		_attachments.Append(Chunk(0, [7], size: 2, last: false));
		_attachments.Append(Chunk(1, [8], size: 2, last: true));

		Assert.Equal([7, 8], File.ReadAllBytes(Assert.Single(_attachments.Resolve("repo", [Id])).Path));
	}

	[Fact]
	public void AtMostFiftyUploadsInProgressPerRepo()
	{
		for (var i = 0; i < ChatAttachments.MaxUploadsPerRepo; i++)
		{
			_attachments.Append(Chunk(0, [1], 2, false) with { Id = i.ToString("x32") });
		}

		// A first chunk gets no reply: the refusal reaches the client on the upload's last chunk.
		var extra = "f".PadLeft(32, 'f');
		Assert.Null(_attachments.Append(Chunk(0, [1], 2, false) with { Id = extra }));
		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(1, [2], 2, true) with { Id = extra }));
		Assert.Equal(ErrorCodes.TooLarge, error.Code);
		Assert.Equal("Too many uploads in progress.", error.Message);
		Assert.Null(_attachments.Append(Chunk(0, [1], 2, false) with { Repo = "other" }));
	}

	[Fact]
	public void AbandonedUploads_AreSweptBeforeTheLimit_SoTheyDoNotBlockTheRepo()
	{
		for (var i = 0; i < ChatAttachments.MaxUploadsPerRepo; i++)
		{
			_attachments.Append(Chunk(0, [1], 2, false) with { Id = i.ToString("x32") });
		}

		_time.Advance(TimeSpan.FromHours(25));

		Assert.Null(_attachments.Append(Chunk(0, [1], 2, false)));
		Assert.NotNull(_attachments.Append(Chunk(1, [2], 2, true)));
	}

	[Fact]
	public void AStrayChunk_ForAFinishedUpload_KeepsTheFile()
	{
		_attachments.Append(Chunk(0, [1, 2], size: 2, last: true));

		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(1, [2], size: 2, last: true)));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
		Assert.Single(_attachments.Resolve("repo", [Id]));
	}

	[Fact]
	public void AnIoError_IsInternal_AndAnInvalidName_IsABadRequest()
	{
		var io = new ChatAttachments(new FailingStore(new IOException("disk full")), _time);
		var internalError = Assert.Throws<EnvelopeException>(() => io.Append(Chunk(0, [1], 1, true)));
		Assert.Equal(ErrorCodes.Internal, internalError.Code);
		Assert.Equal("Could not store the attachment.", internalError.Message);

		var invalid = new ChatAttachments(new FailingStore(new ArgumentException("bad name")), _time);
		var badName = Assert.Throws<EnvelopeException>(() => invalid.Append(Chunk(0, [1], 1, true)));
		Assert.Equal(ErrorCodes.BadRequest, badName.Code);
		Assert.Equal("Invalid attachment name.", badName.Message);
	}

	[Fact]
	public void Resolve_UnknownForeignOrBadIds_AreExpired()
	{
		_attachments.Append(Chunk(0, [1], 1, true));

		foreach (var (repo, id) in new[] { ("repo", "ffffffffffffffffffffffffffffffff"), ("other", Id), ("repo", "..") })
		{
			var error = Assert.Throws<EnvelopeException>(() => _attachments.Resolve(repo, [id]));
			Assert.Equal(ErrorCodes.BadRequest, error.Code);
			Assert.Equal(ChatAttachments.Expired, error.Message);
		}
	}

	[Fact]
	public void Resolve_MoreThanTenIds_IsTooLarge() =>
		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _attachments.Resolve("repo", [.. Enumerable.Range(0, 11).Select(i => i.ToString("x32"))])).Code);

	[Fact]
	public void Resolve_DuplicateIds_CountOnce()
	{
		_attachments.Append(Chunk(0, [1], 1, true));

		Assert.Single(_attachments.Resolve("repo", [Id, Id]));
	}

	[Fact]
	public void AnUploadStart_SweepsOldAttachments_AtMostOnceADay()
	{
		_attachments.Append(Chunk(0, [1], 1, true)); // the first upload of this instance sweeps (nothing to delete yet)
		var file = _attachments.Resolve("repo", [Id])[0].Path;
		File.SetLastWriteTimeUtc(file, _time.GetUtcNow().AddDays(-31).UtcDateTime);

		_time.Advance(TimeSpan.FromHours(23));
		_attachments.Append(Chunk(0, [1], 1, true) with { Id = "1".PadLeft(32, '1') });
		Assert.True(File.Exists(file)); // not a day since the last sweep

		_time.Advance(TimeSpan.FromHours(2));
		_attachments.Append(Chunk(0, [1], 1, true) with { Id = "2".PadLeft(32, '2') });
		Assert.False(File.Exists(file));
		Assert.Throws<EnvelopeException>(() => _attachments.Resolve("repo", [Id]));
	}

	[Fact]
	public async Task Handler_AnswersTheLastChunkWithItsCorrelationId_AndNothingElse()
	{
		var router = new EnvelopeRouter([new ChatAttachHandler(_attachments)]);
		var context = new EnvelopeContext("c1", "user@example.com", (_, _) => Task.CompletedTask);

		var first = await router.RouteAsync(Envelope.Create(MessageTypes.ChatAttach, Chunk(0, [1], 2, false)), context, TestContext.Current.CancellationToken);
		var last = await router.RouteAsync(Envelope.Create(MessageTypes.ChatAttach, Chunk(1, [2], 2, true), "corr"), context, TestContext.Current.CancellationToken);
		var bad = await router.RouteAsync(new Envelope(MessageTypes.ChatAttach, JsonDocument.Parse("[]").RootElement, "x"), context, TestContext.Current.CancellationToken);

		Assert.Null(first);
		Assert.Equal(MessageTypes.ChatAttached, last!.Type);
		Assert.Equal("corr", last.CorrelationId);
		Assert.Equal(MessageTypes.Error, bad!.Type);
	}

	private static ChatAttachPayload Chunk(long offset, byte[] data, long size, bool last, string name = "shot.png") =>
		new("repo", Id, name, size, offset, SyncData.Encode(data), last);

	private sealed class FailingStore(Exception failure) : IAttachmentStore
	{
		public string RepoFolder(string repo) => repo;

		public void Begin(string repo, string id, string name)
		{
		}

		public long Append(string repo, string id, string name, byte[] data) => throw failure;

		public void Commit(string repo, string id, string name)
		{
		}

		public StoredAttachment? Find(string repo, string id) => null;

		public void Delete(string repo, string id)
		{
		}

		public int Sweep(DateTimeOffset olderThan, DateTimeOffset partsOlderThan) => 0;
	}
}
