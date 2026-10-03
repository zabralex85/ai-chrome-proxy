using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Tray;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class FileSystemMirrorStoreTests : IDisposable
{
	private const string Repo = "repo";

	private readonly string _temp = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly List<string> _links = [];
	private readonly string _root;
	private readonly string _repoRoot;
	private readonly FileSystemMirrorStore _store;

	public FileSystemMirrorStoreTests()
	{
		_root = Path.Combine(_temp, "mirror");
		_repoRoot = Path.Combine(_root, Repo);
		Directory.CreateDirectory(_repoRoot);
		_store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
	}

	public void Dispose()
	{
		// A junction is removed on its own first: a recursive delete is refused on it.
		_links.ForEach(Directory.Delete);
		Directory.Delete(_temp, recursive: true);
	}

	[Fact]
	public async Task CreateTemp_Commit_FileInPlace_TempGone_HashMatches()
	{
		var content = Encoding.UTF8.GetBytes("hello");
		using (var stream = _store.CreateTemp(Repo, "src/a.txt"))
		{
			await stream.WriteAsync(content, TestContext.Current.CancellationToken);
		}

		Assert.True(File.Exists(Path.Combine(_repoRoot, "src", "a.txt.aicp-tmp")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "a.txt")));

		_store.Commit(Repo, "src/a.txt");

		Assert.Equal(content, File.ReadAllBytes(Path.Combine(_repoRoot, "src", "a.txt")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "a.txt.aicp-tmp")));
		Assert.Equal(Sha(content), await _store.GetHashAsync(Repo, "src/a.txt", TestContext.Current.CancellationToken));
	}

	[Fact]
	public void DiscardTemp_RemovesTemp_KeepsExistingFile()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "old");
		using (var stream = _store.CreateTemp(Repo, "a.txt"))
		{
			stream.WriteByte(1);
		}

		_store.DiscardTemp(Repo, "a.txt");
		_store.DiscardTemp(Repo, "never-created.txt");

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
		Assert.Equal("old", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
	}

	[Fact]
	public async Task GetHash_MissingFileOrDirectory_Null()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "dir"));

		Assert.Null(await _store.GetHashAsync(Repo, "missing.txt", TestContext.Current.CancellationToken));
		Assert.Null(await _store.GetHashAsync(Repo, "dir", TestContext.Current.CancellationToken));
		Assert.Null(await _store.GetHashAsync("other-repo", "a.txt", TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task GetHash_CachedBySizeAndWriteTime()
	{
		var ct = TestContext.Current.CancellationToken;
		var file = Path.Combine(_repoRoot, "a.txt");
		File.WriteAllText(file, "aaaa");
		var written = File.GetLastWriteTimeUtc(file);
		var first = await _store.GetHashAsync(Repo, "a.txt", ct);

		// Same size and write time: the cached hash is returned without reading the file.
		File.WriteAllText(file, "bbbb");
		File.SetLastWriteTimeUtc(file, written);
		Assert.Equal(first, await _store.GetHashAsync(Repo, "a.txt", ct));

		File.SetLastWriteTimeUtc(file, written.AddSeconds(5));
		Assert.Equal(Sha(Encoding.UTF8.GetBytes("bbbb")), await _store.GetHashAsync(Repo, "a.txt", ct));
	}

	[Fact]
	public void ListFiles_RelativeSlashPaths_IncludesHidden_SkipsTempFilesAndJunctions()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Directory.CreateDirectory(Path.Combine(_repoRoot, "src", "deep"));
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "a");
		File.WriteAllText(Path.Combine(_repoRoot, "src", "deep", "b.cs"), "b");
		File.WriteAllText(Path.Combine(_repoRoot, ".hidden"), "h");
		File.SetAttributes(Path.Combine(_repoRoot, ".hidden"), FileAttributes.Hidden);
		File.WriteAllText(Path.Combine(_repoRoot, "c.txt.aicp-tmp"), "t");
		Junction(Path.Combine(_repoRoot, "link"), outside);

		var files = _store.ListFiles(Repo).Order(StringComparer.Ordinal).ToList();

		Assert.Equal([".hidden", "a.txt", "src/deep/b.cs"], files);
	}

	[Fact]
	public void ListFiles_NoRepoFolder_Empty()
	{
		Assert.Empty(_store.ListFiles("not-synced-yet"));
	}

	[Fact]
	public void Delete_RemovesFileAndEmptyParents_KeepsRepoFolderAndNonEmptyParents()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "a", "b", "c"));
		File.WriteAllText(Path.Combine(_repoRoot, "a", "keep.txt"), "k");
		File.WriteAllText(Path.Combine(_repoRoot, "a", "b", "c", "x.txt"), "x");

		_store.Delete(Repo, "a/b/c/x.txt");

		Assert.False(Directory.Exists(Path.Combine(_repoRoot, "a", "b")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a", "keep.txt")));

		_store.Delete(Repo, "a/keep.txt");

		Assert.False(Directory.Exists(Path.Combine(_repoRoot, "a")));
		Assert.True(Directory.Exists(_repoRoot));
	}

	[Fact]
	public void Delete_Missing_NoError()
	{
		_store.Delete(Repo, "missing/file.txt");

		Assert.True(Directory.Exists(_repoRoot));
	}

	[Fact]
	public void JunctionInsideMirror_NotFollowed_ByDeleteOrWrite()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Junction(Path.Combine(_repoRoot, "link"), outside);

		var delete = Assert.Throws<EnvelopeException>(() => _store.Delete(Repo, "link/secret.txt"));
		var write = Assert.Throws<EnvelopeException>(() => _store.CreateTemp(Repo, "link/new.txt"));

		Assert.Equal(ErrorCodes.BadRequest, delete.Code);
		Assert.Equal(ErrorCodes.BadRequest, write.Code);
		Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
		Assert.False(File.Exists(Path.Combine(outside, "new.txt.aicp-tmp")));
	}

	[Fact]
	public void RepoFolderIsJunction_Refused()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Junction(Path.Combine(_root, "linked"), outside);

		Assert.Empty(_store.ListFiles("linked"));
		Assert.Throws<EnvelopeException>(() => _store.Delete("linked", "secret.txt"));
		Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
	}

	[Theory]
	[InlineData(Repo, "../outside.txt")]
	[InlineData(Repo, "a\\b.txt")]
	[InlineData(Repo, "C:/Windows/win.ini")]
	[InlineData(Repo, "a.txt.aicp-tmp")]
	[InlineData("..", "a.txt")]
	[InlineData("a/b", "a.txt")]
	[InlineData("", "a.txt")]
	public void InvalidRepoOrPath_BadRequest(string repo, string path)
	{
		var ex = Assert.Throws<EnvelopeException>(() => _store.Delete(repo, path));

		Assert.Equal(ErrorCodes.BadRequest, ex.Code);
	}

	[Fact]
	public void ShortNameOfDotGit_Refused_NothingWrittenToHooks()
	{
		var hooks = Path.Combine(_repoRoot, ".git", "hooks");
		Directory.CreateDirectory(hooks);

		var write = Assert.Throws<EnvelopeException>(() => _store.CreateTemp(Repo, "GIT~1/hooks/x"));
		var delete = Assert.Throws<EnvelopeException>(() => _store.Delete(Repo, "GIT~1/hooks/x"));

		Assert.Equal(ErrorCodes.BadRequest, write.Code);
		Assert.Equal(ErrorCodes.BadRequest, delete.Code);
		Assert.Empty(Directory.EnumerateFileSystemEntries(hooks));
	}

	[Fact]
	public void FullPathIn_CanonicalPath_Combined()
	{
		Assert.Equal(Path.Combine(_repoRoot, "a", "b.txt"), FileSystemMirrorStore.FullPathIn(_repoRoot, "a/b.txt"));
	}

	// Paths the validator already rejects, passed straight to the second line of defence: Win32 normalization rewrites each of them.
	[Theory]
	[InlineData("a/./b.txt")]
	[InlineData("a/b.txt.")]
	[InlineData("a/b.txt ")]
	[InlineData("../repo/a.txt")]
	[InlineData("../outside.txt")]
	public void FullPathIn_RewrittenOrOutside_BadRequest(string path)
	{
		var ex = Assert.Throws<EnvelopeException>(() => FileSystemMirrorStore.FullPathIn(_repoRoot, path));

		Assert.Equal(ErrorCodes.BadRequest, ex.Code);
	}

	[Fact]
	public void ResolveRoot_ConfiguredDataDirOrContentRoot()
	{
		var content = Path.Combine(_temp, "content");
		var dataDir = new DataDirectory(Path.Combine(_temp, "data"));

		Assert.Equal(Path.Combine(_temp, "m"), MirrorOptions.ResolveRoot(Path.Combine(_temp, "m"), dataDir, content));
		Assert.Equal(Path.Combine(content, "rel"), MirrorOptions.ResolveRoot("rel", dataDir, content));
		Assert.Equal(Path.Combine(_temp, "data", "mirror"), MirrorOptions.ResolveRoot(" ", dataDir, content));
		Assert.Equal(Path.Combine(content, "data", "mirror"), MirrorOptions.ResolveRoot(null, null, content));
	}

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private void Junction(string link, string target)
	{
		ServiceSetupSecurityTests.Junction(link, target);
		_links.Add(link);
	}
}
