using System.Security.Cryptography;
using AiChromeProxy.Client.Packages;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>The packages engine against <see cref="LoopbackServer"/> (the real <c>PackageUploads</c> and store), a synced <see cref="FakeFolder"/> repo and a <see cref="FakePackageFolder"/> cache.</summary>
public sealed class PackagesEngineTests : IDisposable
{
	private const string Corp = "https://corp.example/nuget/v3/index.json";
	private const string NuGetOrg = "https://api.nuget.org/v3/index.json";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _repo = new();
	private readonly FakePackageFolder _cache = new() { Remembered = true };
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
	private readonly SyncEngine _sync;
	private readonly PackagesEngine _engine;

	public PackagesEngineTests()
	{
		_sync = new SyncEngine(_server.Transport, _repo, TimeProvider.System);
		_engine = new PackagesEngine(_server.Transport, _sync, _repo, _cache, _time);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task APrivatePackage_IsUploaded_WithEveryFile()
	{
		Assets("App/App.csproj", ("Corp.A", "1.2.3"));
		var files = Private("corp.a", "1.2.3", nupkgSize: 100);
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal([new PackageRow("corp.a", "1.2.3", PackageState.OnServer, "On the server")], _engine.Packages);
		Assert.Equal([new ProjectRow("App/App.csproj", null)], _engine.Projects);
		var folder = Path.Combine(_server.PackagesRoot, "nuget", "corp.a", "1.2.3");
		foreach (var (path, bytes) in files)
		{
			Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, path)));
		}

		Assert.Equal("1 private packages ready on the server · 0 missing", _engine.Summary);
	}

	[Fact]
	public async Task APublicPackage_IsPublic_AndNothingIsSent()
	{
		Assets("App.csproj", ("Newtonsoft.Json", "13.0.3"));
		_cache.Write("newtonsoft.json/13.0.3/.nupkg.metadata", Metadata(new byte[10], NuGetOrg));
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal([new PackageRow("newtonsoft.json", "13.0.3", PackageState.Public, "Public")], _engine.Packages);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type.StartsWith("packages.", StringComparison.Ordinal));
		Assert.Equal(string.Empty, _engine.Summary);
	}

	[Fact]
	public async Task Projects_WithoutRestore_OrWithBadAssets_SaySo_AndPackagesNotCached_AreNotOnThisComputer()
	{
		_repo.Write("A/A.csproj", "<Project />");
		_repo.Write("B/B.csproj", "<Project />");
		_repo.Write("B/obj/project.assets.json", "{ broken");
		Assets("C/C.CSPROJ", ("Corp.Missing", "2.0.0"));
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal(
			[new ProjectRow("A/A.csproj", "No restore yet"), new ProjectRow("B/B.csproj", "Could not read obj/project.assets.json"), new ProjectRow("C/C.CSPROJ", null)],
			_engine.Projects);
		Assert.Equal([new PackageRow("corp.missing", "2.0.0", PackageState.NotOnComputer, "Not on this computer")], _engine.Packages);
		Assert.Equal("0 private packages ready on the server · 1 missing", _engine.Summary);
	}

	[Fact]
	public async Task AnAssetsFileThatCannotBeRead_IsAProblemToo()
	{
		Assets("App.csproj", ("Corp.A", "1.0.0"));
		_repo.HashFailures.Add("obj/project.assets.json");
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal([new ProjectRow("App.csproj", "Could not read obj/project.assets.json")], _engine.Projects);
		Assert.Empty(_engine.Packages);
	}

	[Fact]
	public async Task AnAssetsFileWithATimeButNoText_CouldNotBeRead()
	{
		_repo.Write("App.csproj", "<Project />");
		_repo.Modified["obj/project.assets.json"] = 5;
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal([new ProjectRow("App.csproj", "Could not read obj/project.assets.json")], _engine.Projects);
	}

	[Fact]
	public async Task AnAssetsFile_IsReadAgain_OnlyWhenItsModifiedTimeChanged()
	{
		Assets("App.csproj", ("Corp.A", "1.0.0"));
		_repo.Modified["obj/project.assets.json"] = 5;
		await ReadyAsync();
		await _engine.RefreshAsync(Ct);

		Assets("App.csproj", ("Corp.B", "1.0.0"));
		await _engine.RefreshAsync(Ct);
		Assert.Equal(["corp.a"], _engine.Packages.Select(p => p.Id));

		_repo.Modified["obj/project.assets.json"] = 6;
		await _engine.RefreshAsync(Ct);
		Assert.Equal(["corp.b"], _engine.Packages.Select(p => p.Id));
	}

	[Fact]
	public async Task ASecondRefresh_WithThePackageOnTheServer_BeginsNothing()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		await _engine.RefreshAsync(Ct);

		await _engine.RefreshAsync(Ct);

		Assert.Equal(1, _server.Transport.Sent.Count(e => e.Type == MessageTypes.PackagesNuGetBegin));
		Assert.Equal(2, _server.Transport.Sent.Count(e => e.Type == MessageTypes.PackagesNuGetHave));
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task ANupkgThatDoesNotMatchItsHash_Fails_AndIsTriedAgainOnTheNextPass()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Write("corp.a/1.2.3/.nupkg.metadata", Metadata([9, 9, 9], Corp));
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);
		Assert.Equal([new PackageRow("corp.a", "1.2.3", PackageState.Failed, "Failed: Changed while uploading; retried on the next pass.")], _engine.Packages);
		Assert.Equal("0 private packages ready on the server · 1 missing", _engine.Summary);

		_cache.Write("corp.a/1.2.3/.nupkg.metadata", Metadata(_cache.Files["corp.a/1.2.3/corp.a.1.2.3.nupkg"], Corp));
		await _engine.RefreshAsync(Ct);
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task AFileShorterThanListed_Fails()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Sizes["corp.a/1.2.3/lib/net10.0/a.dll"] = 50_000;
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal("Failed: The package changed while it was uploaded.", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task OverTwoThousandFiles_IsTooLarge_AndNothingIsSent()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		foreach (var i in Enumerable.Range(0, 2_000))
		{
			_cache.Files[$"corp.a/1.2.3/content/f{i}.txt"] = [1];
		}

		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		var row = Assert.Single(_engine.Packages);
		Assert.Equal(PackageState.TooLarge, row.State);
		Assert.Equal("Too large to upload (more than 2 000 files)", row.Text);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.PackagesNuGetBegin);
	}

	[Fact]
	public async Task AFailedUpload_WaitsFiveMinutes_KeepingItsRow_ThenIsTriedAgain()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Sizes["corp.a/1.2.3/lib/net10.0/a.dll"] = 50_000;
		await ReadyAsync();
		await _engine.RefreshAsync(Ct);
		_cache.Sizes.Clear();

		_time.Advance(PackagesEngine.RetryAfter - TimeSpan.FromSeconds(1));
		await _engine.RefreshAsync(Ct);
		Assert.Equal("Failed: The package changed while it was uploaded.", Assert.Single(_engine.Packages).Text);
		Assert.Equal(1, Begins());

		_time.Advance(TimeSpan.FromSeconds(1));
		await _engine.RefreshAsync(Ct);
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
		Assert.Equal(2, Begins());
	}

	[Fact]
	public async Task AFailedUpload_IsTriedAgainAtOnce_AfterTheSourceIsAddedAgain()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Sizes["corp.a/1.2.3/lib/net10.0/a.dll"] = 50_000;
		await ReadyAsync();
		await _engine.RefreshAsync(Ct);
		_cache.Sizes.Clear();

		await _engine.AddSourceAsync();

		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task AFailedUpload_IsTriedAgainAtOnce_AfterAllow()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Sizes["corp.a/1.2.3/lib/net10.0/a.dll"] = 50_000;
		await ReadyAsync();
		await _engine.RefreshAsync(Ct);
		_cache.Sizes.Clear();

		await _engine.AllowAsync();

		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task AnUploadLostWithTheConnection_IsTriedAgainOnTheNextPass()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		var reply = _server.Transport.Reply!;
		_server.Transport.Reply = e =>
		{
			if (e.Type == MessageTypes.PackagesNuGetChunk)
			{
				_server.Transport.SetState(TransportState.Reconnecting);
				throw new InvalidOperationException("gone");
			}

			return reply(e);
		};
		await _engine.RefreshAsync(Ct);
		Assert.Equal($"Failed: {SyncEngine.ConnectionLost}", Assert.Single(_engine.Packages).Text);
		_server.Transport.Reply = reply;
		_server.Reconnect();

		// The sync pass after the reconnect starts the next packages pass.
		await _sync.SyncOnceAsync(Ct);

		await UntilAsync(() => _engine.Packages is [{ State: PackageState.OnServer }]);
	}

	[Fact]
	public async Task ASourceChange_DuringAPass_RunsOneMorePass()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		var picked = false;
		_cache.OnList = async () =>
		{
			if (!picked)
			{
				picked = true;
				await _engine.AddSourceAsync();
			}
		};

		await _engine.RefreshAsync(Ct);

		Assert.True(picked);
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task AListedPathTheProtocolRefuses_Fails_AndNothingIsSent()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		var path = "content/" + new string('a', 300) + ".txt";
		_cache.Files[$"corp.a/1.2.3/{path}"] = [1];
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal(new PackageRow("corp.a", "1.2.3", PackageState.Failed, $"Failed: {SyncPath.GetError(path)}"), Assert.Single(_engine.Packages));
		Assert.Equal(0, Begins());

		// The usual back-off: not tried again on the next pass.
		await _engine.RefreshAsync(Ct);
		Assert.Equal(0, Begins());
	}

	[Fact]
	public async Task OverTwoHundredFiftyMegabytes_IsTooLarge()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Sizes["corp.a/1.2.3/corp.a.1.2.3.nupkg"] = 300L * 1024 * 1024;
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal(new PackageRow("corp.a", "1.2.3", PackageState.TooLarge, "Too large to upload (300.1 MB > 250 MB)"), Assert.Single(_engine.Packages));
	}

	[Fact]
	public async Task JustOverTwoHundredFiftyMegabytes_ShowsTheSizeRoundedUp()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		var files = Private("corp.a", "1.2.3", nupkgSize: 10);
		_cache.Sizes["corp.a/1.2.3/corp.a.1.2.3.nupkg"] = PackageLimits.MaxPackageBytes + 1 - files.Where(f => !f.Key.EndsWith(".nupkg", StringComparison.Ordinal)).Sum(f => f.Value.Length);
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal("Too large to upload (250.1 MB > 250 MB)", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task APackageOfEmptyFiles_IsUploaded()
	{
		Assets("App.csproj", ("Corp.E", "1.0.0"));
		_cache.Files["corp.e/1.0.0/corp.e.1.0.0.nupkg"] = [];
		_cache.Write("corp.e/1.0.0/.nupkg.metadata", Metadata([], Corp));
		_cache.Sizes["corp.e/1.0.0/.nupkg.metadata"] = 0;
		foreach (var i in Enumerable.Range(0, 20))
		{
			_cache.Files[$"corp.e/1.0.0/content/f{i:00}.txt"] = [];
		}

		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal("On the server", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task Changed_IsRaisedAsEachPackageFinishes()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"), ("Corp.B", "1.0.0"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		Private("corp.b", "1.0.0", nupkgSize: 10);
		await ReadyAsync();
		var seen = new List<IReadOnlyList<PackageRow>>();
		_engine.Changed += () => seen.Add(_engine.Packages);

		await _engine.RefreshAsync(Ct);

		Assert.Contains(seen, rows => rows is [{ State: PackageState.OnServer }, { State: PackageState.Uploading }]);
		Assert.Contains(seen, rows => rows is [{ State: PackageState.Uploading }, { State: PackageState.Uploading }]);
	}

	[Fact]
	public async Task RemovingTheSource_MidPass_StopsUploading()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"), ("Corp.B", "1.0.0"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		Private("corp.b", "1.0.0", nupkgSize: 10);
		await ReadyAsync();
		_cache.OnList = () => _engine.RemoveSourceAsync();

		await _engine.RefreshAsync(Ct);

		Assert.All(_engine.Packages, r => Assert.Equal("Not on this computer", r.Text));
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.PackagesNuGetBegin);
	}

	[Fact]
	public async Task NotFound_IsTriedAgain_AndReadyOnceTheCacheReads()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		_cache.ReadFailure = new JSException("NotFoundError: gone.");
		await _engine.RefreshAsync(Ct);
		Assert.Equal(SourceState.NotFound, _engine.Source);

		_cache.ReadFailure = null;
		await _engine.RefreshAsync(Ct);

		Assert.Equal(SourceState.Ready, _engine.Source);
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task UnreadableMetadata_Fails()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		_cache.Write("corp.a/1.2.3/.nupkg.metadata", "garbage");
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal("Failed: unreadable .nupkg.metadata", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task AServerRefusal_OfHave_FailsTheCandidates()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		_server.Transport.Reply = e => Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Error, new ErrorPayload(ErrorCodes.TooLarge, "Too many."), e.CorrelationId));

		await _engine.RefreshAsync(Ct);

		Assert.Equal("Failed: Too many.", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task AConnectionLost_DuringTheUpload_Fails_WithConnectionLost()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		var reply = _server.Transport.Reply!;
		_server.Transport.Reply = async e =>
		{
			if (e.Type == MessageTypes.PackagesNuGetChunk)
			{
				_server.Transport.SetState(TransportState.Reconnecting);
				throw new InvalidOperationException("gone");
			}

			return await reply(e);
		};

		await _engine.RefreshAsync(Ct);

		Assert.Equal($"Failed: {SyncEngine.ConnectionLost}", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task WithoutASource_RowsAreNotOnThisComputer_ThenAddSourceUploads_AndRemoveSourceForgetsIt()
	{
		_cache.Remembered = false;
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		await _engine.RefreshAsync(Ct);
		Assert.Equal(SourceState.None, _engine.Source);
		Assert.Equal("Not on this computer", Assert.Single(_engine.Packages).Text);
		Assert.Equal(string.Empty, _engine.Summary);

		var changes = 0;
		_engine.Changed += () => changes++;
		await _engine.AddSourceAsync();
		Assert.Equal(SourceState.Ready, _engine.Source);
		Assert.Equal("packages", _engine.SourceName);
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
		Assert.True(changes > 0);

		await _engine.RemoveSourceAsync();
		Assert.Equal(SourceState.None, _engine.Source);
		Assert.Null(_engine.SourceName);
		Assert.True(_cache.Forgotten);
		Assert.Equal("Not on this computer", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task AddSource_Cancelled_ChangesNothing()
	{
		_cache.Remembered = false;
		_cache.PickResult = null;
		await _engine.InitializeAsync();

		await _engine.AddSourceAsync();

		Assert.Equal(SourceState.None, _engine.Source);
	}

	[Fact]
	public async Task ARememberedSource_WithoutPermission_NeedsIt_ThenAllowMakesItReady()
	{
		_cache.Granted = false;
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();
		Assert.Equal(SourceState.NeedsPermission, _engine.Source);
		Assert.Equal("packages", _engine.SourceName);

		await _engine.AllowAsync();

		Assert.Equal(SourceState.Ready, _engine.Source);
		Assert.Equal(PackageState.OnServer, Assert.Single(_engine.Packages).State);
	}

	[Fact]
	public async Task ASourceThatCannotBeRead_IsNotFound()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		await ReadyAsync();
		_cache.ReadFailure = new JSException("NotFoundError: gone.");

		await _engine.RefreshAsync(Ct);

		Assert.Equal(SourceState.NotFound, _engine.Source);
		Assert.Equal("Not on this computer", Assert.Single(_engine.Packages).Text);
	}

	[Fact]
	public async Task OnServerAndMissing_AreSummed()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"), ("Corp.B", "1.0.0"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await ReadyAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Equal("1 private packages ready on the server · 1 missing", _engine.Summary);
		Assert.Equal(["corp.a", "corp.b"], _engine.Packages.Select(p => p.Id));
	}

	[Fact]
	public async Task ASyncPass_StartsARefresh()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		Private("corp.a", "1.2.3", nupkgSize: 10);
		await _engine.InitializeAsync();

		await SyncedAsync();

		await UntilAsync(() => _engine.Packages is [{ State: PackageState.OnServer }]);
	}

	[Fact]
	public async Task ManyPrivatePackages_AreAskedAboutInPages_EachUnderTheSignalRLimit()
	{
		var packages = Enumerable.Range(0, 300).Select(i => ($"Corp.{i:D3}.{new string('x', 90)}", "1.0.0")).ToArray();
		Assets("App.csproj", packages);
		foreach (var (id, version) in packages)
		{
			_cache.Write($"{id.ToLowerInvariant()}/{version}/.nupkg.metadata", Metadata([1], Corp));
		}

		await ReadyAsync();
		_server.Transport.Reply = e => Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.PackagesNuGetMissing, new NuGetMissingPayload([]), e.CorrelationId));

		await _engine.RefreshAsync(Ct);

		var haves = _server.Transport.Sent.Where(e => e.Type == MessageTypes.PackagesNuGetHave).ToList();
		Assert.True(haves.Count > 1);
		Assert.All(haves, e => Assert.True(ManifestPlannerTests.WireSize(e) < ManifestPlannerTests.SignalRLimit));
		Assert.Equal(300, _engine.Packages.Count(r => r.State == PackageState.OnServer));
	}

	[Fact]
	public async Task NothingHappens_WithoutAnOpenRepo()
	{
		Assets("App.csproj", ("Corp.A", "1.2.3"));
		await _engine.InitializeAsync();

		await _engine.RefreshAsync(Ct);

		Assert.Empty(_engine.Projects);
		Assert.Empty(_engine.Packages);
	}

	private static string Metadata(byte[] nupkg, string source) =>
		$$"""{"version":2,"contentHash":"{{Convert.ToBase64String(SHA512.HashData(nupkg))}}","source":"{{source}}"}""";

	private static async Task UntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the packages engine.");
			await Task.Delay(10, Ct);
		}
	}

	private int Begins() => _server.Transport.Sent.Count(e => e.Type == MessageTypes.PackagesNuGetBegin);

	/// <summary>A project at <paramref name="project"/> whose <c>obj/project.assets.json</c> lists the packages and a project reference.</summary>
	private void Assets(string project, params (string Id, string Version)[] packages)
	{
		_repo.Write(project, "<Project />");
		var dir = Path.GetDirectoryName(project)!.Replace('\\', '/');
		var libraries = string.Join(",", packages.Select(p => $$"""
			"{{p.Id}}/{{p.Version}}":{"type":"package","path":"{{p.Id.ToLowerInvariant()}}/{{p.Version}}"}
			"""));
		_repo.Write(dir.Length == 0 ? "obj/project.assets.json" : $"{dir}/obj/project.assets.json", "{\"version\":3,\"libraries\":{" + libraries + ",\"Lib/1.0.0\":{\"type\":\"project\"}}}");
	}

	/// <summary>A private package in the cache: <c>.nupkg</c>, its metadata (corp source), a dll over 16 KB and an empty file.</summary>
	private Dictionary<string, byte[]> Private(string id, string version, int nupkgSize)
	{
		var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			[$"{id}.{version}.nupkg"] = [.. Enumerable.Range(0, nupkgSize).Select(i => (byte)(i * 7))],
			["lib/net10.0/a.dll"] = [.. Enumerable.Range(0, 40_000).Select(i => (byte)i)],
			["empty.txt"] = [],
		};
		files[".nupkg.metadata"] = System.Text.Encoding.UTF8.GetBytes(Metadata(files[$"{id}.{version}.nupkg"], Corp));
		foreach (var (path, bytes) in files)
		{
			_cache.Files[$"{id}/{version}/{path}"] = bytes;
		}

		return files;
	}

	private async Task SyncedAsync()
	{
		await _sync.InitializeAsync();
		await _sync.OpenFolderAsync();
		await _sync.SyncOnceAsync(Ct);
	}

	private async Task ReadyAsync()
	{
		await SyncedAsync();
		await _engine.InitializeAsync();
	}
}
