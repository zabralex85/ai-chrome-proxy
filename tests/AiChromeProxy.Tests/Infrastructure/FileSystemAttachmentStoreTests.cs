using AiChromeProxy.Infrastructure.Chat;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tests.Tray;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class FileSystemAttachmentStoreTests : IDisposable
{
	private const string Id = "0123456789abcdef0123456789abcdef";
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FileSystemAttachmentStore _store;

	public FileSystemAttachmentStoreTests() => _store = new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = _root }));

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void BeginAppendCommit_LeavesTheNamedFile_AndFindReturnsIt()
	{
		_store.Begin("repo", Id, "shot.png");
		Assert.Equal(2, _store.Append("repo", Id, "shot.png", [1, 2]));
		Assert.Null(_store.Find("repo", Id));
		Assert.Equal(3, _store.Append("repo", Id, "shot.png", [3]));

		_store.Commit("repo", Id, "shot.png");

		var found = _store.Find("repo", Id)!;
		Assert.Equal(Path.Combine(_root, "repo", Id, "shot.png"), found.Path);
		Assert.Equal("shot.png", found.Name);
		Assert.Equal(3, found.Size);
		Assert.Equal([1, 2, 3], File.ReadAllBytes(found.Path));
	}

	[Fact]
	public void Begin_AgainStartsOver()
	{
		_store.Begin("repo", Id, "a.txt");
		_store.Append("repo", Id, "a.txt", [1, 2, 3]);
		_store.Commit("repo", Id, "a.txt");

		_store.Begin("repo", Id, "b.txt");

		Assert.Null(_store.Find("repo", Id));
		Assert.Equal(["b.txt.part"], Directory.GetFiles(Path.Combine(_root, "repo", Id)).Select(Path.GetFileName));
	}

	[Fact]
	public void Delete_RemovesTheFolder_AndIgnoresAMissingOne()
	{
		_store.Begin("repo", Id, "a.txt");

		_store.Delete("repo", Id);
		_store.Delete("repo", Id);

		Assert.False(Directory.Exists(Path.Combine(_root, "repo", Id)));
	}

	[Fact]
	public void RepoFolder_IsUnderTheRoot_AndPathsLeavingItThrow()
	{
		Assert.Equal(Path.Combine(_root, "repo"), _store.RepoFolder("repo"));
		Assert.Throws<ArgumentException>(() => _store.RepoFolder(".."));
		Assert.Throws<ArgumentException>(() => _store.Begin("repo", "..", "a.txt"));
		Assert.Throws<ArgumentException>(() => _store.Begin("repo", Id, "..\\..\\..\\x.txt"));
	}

	[Fact]
	public void Sweep_DeletesOldFolders_AndStaleUploads_KeepsTheRest()
	{
		var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
		var old = Finished("11111111111111111111111111111111", now.AddDays(-31));
		var recent = Finished("22222222222222222222222222222222", now.AddDays(-29));
		var stalePart = Started("33333333333333333333333333333333", now.AddDays(-2));
		var freshPart = Started("44444444444444444444444444444444", now.AddHours(-1));

		var deleted = _store.Sweep(now.AddDays(-30), now.AddDays(-1));

		Assert.Equal(2, deleted);
		Assert.False(Directory.Exists(old));
		Assert.True(Directory.Exists(recent));
		Assert.False(Directory.Exists(stalePart));
		Assert.True(Directory.Exists(freshPart));
	}

	[Theory]
	[InlineData("")]
	[InlineData(".")]
	[InlineData("a\\b")]
	public void BadIds_Throw(string id)
	{
		Assert.Throws<ArgumentException>(() => _store.Begin("repo", id, "a.txt"));
		Assert.Throws<ArgumentException>(() => _store.Find("repo", id));
		Assert.Throws<ArgumentException>(() => _store.RepoFolder("a\\b"));
	}

	[Fact]
	public void Name_CannotReachASiblingAttachmentFolder()
	{
		const string Other = "ffffffffffffffffffffffffffffffff";
		_store.Begin("repo", Other, "keep.txt");
		_store.Begin("repo", Id, "a.txt");

		Assert.Throws<ArgumentException>(() => _store.Begin("repo", Id, "..\\" + Other + "\\x"));
		Assert.Throws<ArgumentException>(() => _store.Append("repo", Id, "..\\" + Other + "\\x", [1]));
		Assert.Throws<ArgumentException>(() => _store.Commit("repo", Id, "sub\\x"));
		Assert.Equal(["keep.txt.part"], Directory.GetFiles(Path.Combine(_root, "repo", Other)).Select(Path.GetFileName));
	}

	[Fact]
	public void RootWithTrailingSeparator_RoundTrips()
	{
		var store = new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = _root + Path.DirectorySeparatorChar }));

		store.Begin("repo", Id, "a.txt");
		Assert.Equal(1, store.Append("repo", Id, "a.txt", [7]));
		store.Commit("repo", Id, "a.txt");

		Assert.Equal(Path.Combine(_root, "repo", Id, "a.txt"), store.Find("repo", Id)!.Path);
	}

	[Fact]
	public void Sweep_SkipsAFolderHeldOpen_AndDeletesTheRest()
	{
		var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
		var held = Finished("11111111111111111111111111111111", now.AddDays(-31));
		var gone = Finished("22222222222222222222222222222222", now.AddDays(-31));

		using (new FileStream(Path.Combine(held, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
		{
			Assert.Equal(1, _store.Sweep(now.AddDays(-30), now.AddDays(-1)));
		}

		Assert.True(Directory.Exists(held));
		Assert.False(Directory.Exists(gone));
	}

	[Fact]
	public void Sweep_WithoutARoot_DeletesNothing() => Assert.Equal(0, _store.Sweep(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

	[Fact]
	public void ResolveRoot_ConfiguredDataDirOrContentRoot()
	{
		var content = Path.Combine(_root, "content");
		var dataDir = new DataDirectory(Path.Combine(_root, "data"));

		Assert.Equal(Path.Combine(_root, "a"), AttachmentsOptions.ResolveRoot(Path.Combine(_root, "a"), dataDir, content));
		Assert.Equal(Path.Combine(content, "rel"), AttachmentsOptions.ResolveRoot("rel", dataDir, content));
		Assert.Equal(Path.Combine(_root, "data", "attachments"), AttachmentsOptions.ResolveRoot(null, dataDir, content));
		Assert.Equal(Path.Combine(content, "data", "attachments"), AttachmentsOptions.ResolveRoot(" ", null, content));
	}

	private string Finished(string id, DateTimeOffset written)
	{
		_store.Begin("repo", id, "a.txt");
		_store.Append("repo", id, "a.txt", [1]);
		_store.Commit("repo", id, "a.txt");
		File.SetLastWriteTimeUtc(Path.Combine(_root, "repo", id, "a.txt"), written.UtcDateTime);
		return Path.Combine(_root, "repo", id);
	}

	private string Started(string id, DateTimeOffset written)
	{
		_store.Begin("repo", id, "a.txt");
		File.SetLastWriteTimeUtc(Path.Combine(_root, "repo", id, "a.txt.part"), written.UtcDateTime);
		return Path.Combine(_root, "repo", id);
	}
}
