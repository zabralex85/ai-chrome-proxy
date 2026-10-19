using System.Security.Cryptography;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Packages;
using AiChromeProxy.Tests.Tray;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class FileSystemPackageStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FileSystemPackageStore _store;

	public FileSystemPackageStoreTests() => _store = new FileSystemPackageStore(Options.Create(new PackagesOptions { Root = _root }));

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void BeginAppendCommit_PutsThePackageInPlace()
	{
		var upload = _store.Begin("corp.a", "1.2.3", "t1");
		_store.Append(upload, "corp.a.1.2.3.nupkg", [1, 2], create: true);
		_store.Append(upload, "corp.a.1.2.3.nupkg", [3], create: false);
		_store.Append(upload, "lib/net10.0/a.dll", [9], create: true);
		Assert.False(_store.HasNuGet("corp.a", "1.2.3"));

		Assert.Equal(Convert.ToBase64String(SHA512.HashData(new byte[] { 1, 2, 3 })), _store.Sha512(upload, "corp.a.1.2.3.nupkg"));
		Assert.True(_store.Commit(upload, "corp.a", "1.2.3"));

		Assert.True(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Equal([9], File.ReadAllBytes(Path.Combine(_root, "nuget", "corp.a", "1.2.3", "lib", "net10.0", "a.dll")));
		Assert.Equal(Path.Combine(_root, "nuget"), _store.NuGetFolder);
	}

	[Fact]
	public void Commit_WhenThePackageExists_DiscardsTheUpload()
	{
		Commit("corp.a", "1.2.3", "t1");
		var second = _store.Begin("corp.a", "1.2.3", "t2");

		Assert.False(_store.Commit(second, "corp.a", "1.2.3"));
		Assert.False(Directory.Exists(second));
	}

	[Fact]
	public void PathsLeavingTheirFolder_Throw()
	{
		var upload = _store.Begin("corp.a", "1.2.3", "t1");
		Assert.Throws<ArgumentException>(() => _store.Append(upload, "../x.dll", [1], create: true));
		Assert.Throws<ArgumentException>(() => _store.Append(upload, "C:/x.dll", [1], create: true));
		Assert.Throws<ArgumentException>(() => _store.Begin("..", "1.0.0", "t"));
		Assert.Throws<ArgumentException>(() => _store.Begin("corp.a", "..", "t"));
		Assert.Throws<ArgumentException>(() => _store.Discard(Path.Combine(_root, "elsewhere")));
	}

	[Theory]
	[InlineData("corp.a.", "1.0.0")]
	[InlineData("corp.a ", "1.0.0")]
	[InlineData("corp.a", "1.0.")]
	public void Names_WindowsWouldTrim_Throw(string id, string version)
	{
		// Windows drops trailing dots and spaces: "corp.a." would be the folder of "corp.a".
		Assert.Throws<ArgumentException>(() => _store.Begin(id, version, "t"));
		Assert.Throws<ArgumentException>(() => _store.HasNuGet(id, version));
	}

	[Fact]
	public void DeleteTemps_RemovesUnfinishedUploadsOnly()
	{
		Commit("corp.a", "1.2.3", "t1");
		_store.Begin("corp.b", "2.0.0", "t2");

		Assert.Equal(1, _store.DeleteTemps());
		Assert.True(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "nuget", "corp.b")));
	}

	[Fact]
	public void DeleteTemps_KeepsAPackageWhoseVersionEndsInTmp()
	{
		Commit("corp.a", "1.0.0-rc.tmp", "t1");

		Assert.Equal(0, _store.DeleteTemps());
		Assert.True(_store.HasNuGet("corp.a", "1.0.0-rc.tmp"));
		Assert.Throws<ArgumentException>(() => _store.Discard(Path.Combine(_root, "nuget", "corp.a", "1.0.0-rc.tmp")));
	}

	[Fact]
	public void Commit_WithAnotherPackagesHandle_Throws()
	{
		var upload = _store.Begin("corp.a", "1.0.0", "t1");

		Assert.Throws<ArgumentException>(() => _store.Commit(upload, "corp.b", "1.0.0"));
		Assert.Throws<ArgumentException>(() => _store.Commit(upload, "corp.a", "2.0.0"));
		Assert.True(Directory.Exists(upload));
	}

	[Fact]
	public void Commit_WithAVersionThatIsAPrefixOfTheHandlesVersion_Throws()
	{
		var upload = _store.Begin("corp.a", "1.0.0", "t");

		Assert.Throws<ArgumentException>(() => _store.Commit(upload, "corp.a", "1.0"));
		Assert.Throws<ArgumentException>(() => _store.Commit(upload, "corp.a", "1"));
		Assert.True(Directory.Exists(upload));
	}

	[Fact]
	public void Commit_WhenThePackageAppearsMeanwhile_DiscardsTheUpload()
	{
		var upload = _store.Begin("corp.a", "1.0.0", "t1");
		var target = Path.Combine(_root, "nuget", "corp.a", "1.0.0");
		Directory.CreateDirectory(target);
		File.WriteAllBytes(Path.Combine(target, "x"), [1]);

		// The Directory.Exists pre-check also catches this; the IOException path of the race itself is not tested deterministically.
		Assert.False(_store.Commit(upload, "corp.a", "1.0.0"));
		Assert.False(Directory.Exists(upload));
	}

	[Fact]
	public void Sha512_WithAPathEscape_Throws()
	{
		var upload = _store.Begin("corp.a", "1.0.0", "t1");
		Assert.Throws<ArgumentException>(() => _store.Sha512(upload, "../x.dll"));
	}

	[Fact]
	public void Append_WithARootedPath_Throws()
	{
		var upload = _store.Begin("corp.a", "1.0.0", "t1");
		Assert.Throws<ArgumentException>(() => _store.Append(upload, Path.Combine(_root, "x.dll"), [1], create: true));
	}

	[Fact]
	public void Begin_WithTheSameTag_ReplacesTheFolder()
	{
		var first = _store.Begin("corp.a", "1.0.0", "t1");
		_store.Append(first, "old.dll", [1], create: true);

		var second = _store.Begin("corp.a", "1.0.0", "t1");

		Assert.Equal(first, second);
		Assert.Empty(Directory.GetFileSystemEntries(second));
	}

	[Fact]
	public void DeleteTemps_WithoutARoot_IsZero() => Assert.Equal(0, _store.DeleteTemps());

	[Fact]
	public void Discard_IgnoresAMissingFolder()
	{
		var upload = _store.Begin("corp.a", "1.2.3", "t1");
		_store.Discard(upload);
		_store.Discard(upload);
		Assert.False(Directory.Exists(upload));
	}

	[Fact]
	public void ResolveRoot_ConfiguredDataDirOrContentRoot()
	{
		var content = Path.Combine(_root, "content");
		var dataDir = new DataDirectory(Path.Combine(_root, "data"));

		Assert.Equal(Path.Combine(_root, "a"), PackagesOptions.ResolveRoot(Path.Combine(_root, "a"), dataDir, content));
		Assert.Equal(Path.Combine(content, "rel"), PackagesOptions.ResolveRoot("rel", dataDir, content));
		Assert.Equal(Path.Combine(_root, "data", "packages"), PackagesOptions.ResolveRoot(null, dataDir, content));
		Assert.Equal(Path.Combine(content, "data", "packages"), PackagesOptions.ResolveRoot(" ", null, content));
	}

	private void Commit(string id, string version, string tag)
	{
		var upload = _store.Begin(id, version, tag);
		_store.Append(upload, "x.nupkg", [1], create: true);
		_store.Commit(upload, id, version);
	}
}
