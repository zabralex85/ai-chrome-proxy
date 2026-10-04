using System.Diagnostics;
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
	private const string Tag = "0123abcd";

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
		using (var stream = _store.CreateTemp(Repo, "src/a.txt", Tag))
		{
			await stream.WriteAsync(content, TestContext.Current.CancellationToken);
		}

		Assert.True(File.Exists(Path.Combine(_repoRoot, "src", "a.txt.0123abcd.aicp-tmp")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "a.txt")));

		_store.Commit(Repo, "src/a.txt", Tag);

		Assert.Equal(content, File.ReadAllBytes(Path.Combine(_repoRoot, "src", "a.txt")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "a.txt.0123abcd.aicp-tmp")));
		Assert.Equal(Sha(content), await _store.GetHashAsync(Repo, "src/a.txt", TestContext.Current.CancellationToken));
	}

	[Fact]
	public void DiscardTemp_RemovesTemp_KeepsExistingFile()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "old");
		using (var stream = _store.CreateTemp(Repo, "a.txt", Tag))
		{
			stream.WriteByte(1);
		}

		_store.DiscardTemp(Repo, "a.txt", Tag);
		_store.DiscardTemp(Repo, "never-created.txt", Tag);

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.0123abcd.aicp-tmp")));
		Assert.Equal("old", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
	}

	[Fact]
	public async Task CreateTemp_HardLinkAtTempName_Replaced_OutsideFileUnchanged()
	{
		var outside = Path.Combine(_temp, "outside.txt");
		File.WriteAllText(outside, "secret");
		HardLink(Path.Combine(_repoRoot, "a.txt.0123abcd.aicp-tmp"), outside);

		using (var stream = _store.CreateTemp(Repo, "a.txt", Tag))
		{
			await stream.WriteAsync("upload"u8.ToArray(), TestContext.Current.CancellationToken);
		}

		Assert.Equal("secret", File.ReadAllText(outside));
		Assert.Equal("upload", File.ReadAllText(Path.Combine(_repoRoot, "a.txt.0123abcd.aicp-tmp")));
	}

	[Fact]
	public async Task CreateTemp_SamePathFromAnotherSession_WhileTheOldTempIsOpen_Works()
	{
		// After a reconnect the old session may still hold its temp file until the dead connection is noticed.
		var ct = TestContext.Current.CancellationToken;
		using (var old = _store.CreateTemp(Repo, "a.txt", "aaaaaaaa"))
		{
			using (var stream = _store.CreateTemp(Repo, "a.txt", "bbbbbbbb"))
			{
				await stream.WriteAsync("new"u8.ToArray(), ct);
			}

			_store.Commit(Repo, "a.txt", "bbbbbbbb");
		}

		Assert.Equal("new", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a.txt.aaaaaaaa.aicp-tmp")));
	}

	[Fact]
	public async Task GetHash_FileOpenByWriter_Hashed()
	{
		var file = Path.Combine(_repoRoot, "a.txt");
		File.WriteAllText(file, "abc");

		using (new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
		{
			Assert.Equal(Sha("abc"u8.ToArray()), await _store.GetHashAsync(Repo, "a.txt", TestContext.Current.CancellationToken));
		}
	}

	[Fact]
	public void FileSymlinkAsPathComponent_Refused()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		try
		{
			// A file-type link to a folder: Directory.Exists is false for it, the link check must still see it.
			File.CreateSymbolicLink(Path.Combine(_repoRoot, "f"), outside);
		}
		catch (IOException)
		{
			Assert.Skip("Creating symbolic links needs Developer Mode or the SeCreateSymbolicLinkPrivilege.");
		}
		catch (UnauthorizedAccessException)
		{
			Assert.Skip("Creating symbolic links needs Developer Mode or the SeCreateSymbolicLinkPrivilege.");
		}

		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _store.Delete(Repo, "f/secret.txt")).Code);
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _store.CreateTemp(Repo, "f/new.txt", Tag)).Code);
		Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
		Assert.False(File.Exists(Path.Combine(outside, "new.txt.aicp-tmp")));
	}

	[Fact]
	public void DeleteStaleTemps_RemovesLeftovers_KeepsKeptOpenAndBehindJunctions()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "x.aicp-tmp"), "o");
		Junction(Path.Combine(_repoRoot, "link"), outside);
		Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "a");
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt.aicp-tmp"), "stale");
		File.WriteAllText(Path.Combine(_repoRoot, "src", "b.cs.aicp-tmp"), "stale");
		File.WriteAllText(Path.Combine(_repoRoot, "kept.txt.0123abcd.aicp-tmp"), "uploading");
		File.WriteAllText(Path.Combine(_repoRoot, "kept.txt.ffffffff.aicp-tmp"), "stale");
		var open = Path.Combine(_repoRoot, "open.txt.aicp-tmp");

		using (new FileStream(open, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			_store.DeleteStaleTemps(Repo, "kept.txt", Tag);
		}

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "b.cs.aicp-tmp")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a.txt")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "kept.txt.0123abcd.aicp-tmp")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "kept.txt.ffffffff.aicp-tmp")));
		Assert.True(File.Exists(open));
		Assert.True(File.Exists(Path.Combine(outside, "x.aicp-tmp")));

		_store.DeleteStaleTemps("not-synced-yet", null, Tag);
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
		Assert.True(_store.HasFiles(Repo, p => p != "src/deep/b.cs"));
		Assert.False(_store.HasFiles(Repo, p => p is ".hidden" or "a.txt" or "src/deep/b.cs"));
	}

	[Fact]
	public void ListFiles_NoRepoFolder_Empty()
	{
		Assert.Empty(_store.ListFiles("not-synced-yet"));
		Assert.False(_store.HasFiles("not-synced-yet", _ => false));
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
		var write = Assert.Throws<EnvelopeException>(() => _store.CreateTemp(Repo, "link/new.txt", Tag));

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

	[Fact]
	public void RepoFolder_Absolute_NullWhenMissingOrLinked_InvalidRefused()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		Junction(Path.Combine(_root, "linked"), outside);

		Assert.Equal(_repoRoot, _store.RepoFolder(Repo));
		Assert.True(Path.IsPathFullyQualified(_store.RepoFolder(Repo)!));
		Assert.Null(_store.RepoFolder("missing"));
		Assert.Null(_store.RepoFolder("linked"));
		Assert.Throws<EnvelopeException>(() => _store.RepoFolder(".."));
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

		var write = Assert.Throws<EnvelopeException>(() => _store.CreateTemp(Repo, "GIT~1/hooks/x", Tag));
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

	[Fact]
	public async Task ReadAsync_RangeOfFile_EmptyPastTheEnd_NullWhenNoFile_WhileOpenForWriting()
	{
		var ct = TestContext.Current.CancellationToken;
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "hello world");
		Directory.CreateDirectory(Path.Combine(_repoRoot, "dir"));

		using (new FileStream(Path.Combine(_repoRoot, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
		{
			Assert.Equal("hello"u8.ToArray(), await _store.ReadAsync(Repo, "a.txt", 0, 5, ct));
			Assert.Equal("world"u8.ToArray(), await _store.ReadAsync(Repo, "a.txt", 6, 100, ct));
			Assert.Empty((await _store.ReadAsync(Repo, "a.txt", 20, 5, ct))!);
		}

		Assert.Null(await _store.ReadAsync(Repo, "missing.txt", 0, 5, ct));
		Assert.Null(await _store.ReadAsync(Repo, "dir", 0, 5, ct));
		Assert.Equal(11, _store.GetSize(Repo, "a.txt"));
		Assert.Equal(0, _store.GetSize(Repo, "missing.txt"));
		Assert.Equal(0, _store.GetSize(Repo, "dir"));
	}

	[Fact]
	public async Task ReadAsync_ThroughJunction_BadRequest()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Junction(Path.Combine(_repoRoot, "link"), outside);

		var read = await Assert.ThrowsAsync<EnvelopeException>(() => _store.ReadAsync(Repo, "link/secret.txt", 0, 5, TestContext.Current.CancellationToken));
		var size = Assert.Throws<EnvelopeException>(() => _store.GetSize(Repo, "link/secret.txt"));

		Assert.Equal((ErrorCodes.BadRequest, ErrorCodes.BadRequest), (read.Code, size.Code));
	}

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	/// <summary>A hard link needs no privilege, unlike a symbolic link.</summary>
	private static void HardLink(string link, string target)
	{
		using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!)
		{
			process.WaitForExit();
			Assert.Equal(0, process.ExitCode);
		}
	}

	private void Junction(string link, string target)
	{
		ServiceSetupSecurityTests.Junction(link, target);
		_links.Add(link);
	}
}
