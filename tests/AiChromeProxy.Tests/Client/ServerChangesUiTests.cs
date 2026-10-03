using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

/// <summary>What the Explorer banner, the conflict tab and the settings tab show and call, driven through the engine against <see cref="LoopbackServer"/>.</summary>
public sealed class ServerChangesUiTests : IDisposable
{
	private const string Repo = "My_Repo";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly SyncEngine _engine;

	public ServerChangesUiTests()
	{
		_engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task Banner_NoWriteAccess_AllowWriting_ThenApplyAllWhenAutomaticApplyIsOff()
	{
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		Assert.Null(RemoteBanner.Of(_engine));

		await EditMirrorAsync("a.txt", "v2");
		await _engine.SyncOnceAsync(Ct);

		var banner = RemoteBanner.Of(_engine)!;
		Assert.Equal(("1 server change — ", "Allow writing"), (banner.Text, banner.Button));

		await _engine.AllowWritingAsync();
		await _engine.SaveSettingsAsync(new ProjectSettings { ApplyServerChanges = false });
		await _engine.SyncOnceAsync(Ct);

		banner = RemoteBanner.Of(_engine)!;
		Assert.Equal(("1 server change waiting", "Apply all"), (banner.Text, banner.Button));
		Assert.Equal("v1", Text("a.txt"));

		await _engine.ApplyAsync(null);

		Assert.Equal("v2", Text("a.txt"));
		Assert.Null(RemoteBanner.Of(_engine));
	}

	[Fact]
	public async Task Banner_ManyServerDeletes_ApplyAllEvenWithAutomaticApply()
	{
		_folder.WriteAccess = true;
		var paths = Enumerable.Range(0, 30).Select(i => $"f{i:00}.txt").ToList();
		paths.ForEach(p => _folder.Write(p, p));
		await SyncedAsync();
		var deleted = paths.Take(SyncEngine.MassDeleteCount + 1).ToList();
		_server.Transport.Push(Envelope.Create(
			MessageTypes.SyncRemote,
			new SyncRemotePayload(Repo, [.. deleted.Select(p => new RemoteChange(p, null, 0, Sha(p)))])));

		await _engine.SyncOnceAsync(Ct);

		Assert.True(_engine.Settings.ApplyServerChangesOrDefault);
		var banner = RemoteBanner.Of(_engine)!;
		Assert.Equal(("21 server changes waiting", "Apply all"), (banner.Text, banner.Button));
		Assert.Equal(30, _folder.Files.Count);

		await _engine.ApplyAsync(null);

		Assert.Equal(30 - deleted.Count, _folder.Files.Count);
		Assert.Null(RemoteBanner.Of(_engine));
	}

	[Fact]
	public async Task Banner_CountsOnlyWaitingChanges_NotConflicts()
	{
		await ConflictAsync();
		await EditMirrorAsync("b.txt", "new");
		_folder.WriteAccess = false;
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("1 server change — ", RemoteBanner.Of(_engine)!.Text);
		Assert.Equal(1, _engine.ConflictCount);
	}

	[Fact]
	public async Task Conflict_TakeServers_AsksForWriteAccessAndWrites()
	{
		await ConflictAsync();
		_folder.WriteAccess = false;
		await _engine.ApplyAsync(null);
		Assert.False(_engine.CanWrite);
		Assert.Equal("the server's edit", await _engine.ServerTextAsync("a.txt"));

		await _engine.TakeServersAsync("a.txt");

		Assert.Equal("the server's edit", Text("a.txt"));
		Assert.Empty(_engine.Remote);
		Assert.Equal(0, _engine.ConflictCount);
	}

	[Fact]
	public async Task Conflict_KeepMine_UploadsOurs()
	{
		await ConflictAsync();

		await _engine.KeepMineAsync("a.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("mine, edited here", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("mine, edited here", Text("a.txt"));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task Conflict_TabClosesWhenResolved()
	{
		await ConflictAsync();
		var tabs = new TabSet();
		tabs.Show(TabSet.ConflictTab("a.txt"));

		tabs.CloseResolved([.. _engine.Remote.Where(r => r.Status == RemoteStatus.Conflict).Select(r => r.Change.Path)]);
		Assert.Contains(TabSet.ConflictTab("a.txt"), tabs.Open);

		await _engine.KeepMineAsync("a.txt");
		tabs.CloseResolved([.. _engine.Remote.Where(r => r.Status == RemoteStatus.Conflict).Select(r => r.Change.Path)]);

		Assert.DoesNotContain(TabSet.ConflictTab("a.txt"), tabs.Open);
	}

	[Fact]
	public async Task Settings_Save_SendsThemAndEngineReadsThemBack()
	{
		await SyncedAsync();
		var form = new ProjectSettingsForm(_engine.Settings) { Excludes = "*.log", Apply = false };

		await _engine.SaveSettingsAsync(form.ToSettings());

		Assert.Equal(("*.log", false), (_engine.Settings.Excludes, _engine.Settings.ApplyServerChangesOrDefault));
		Assert.False(new ProjectSettingsForm(_engine.Settings).IsDirty);
		Assert.Equal("*.log", _server.Projects.GetSettings(Repo).Excludes);
	}

	[Fact]
	public async Task Settings_SaveWithoutSession_Throws()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.SaveSettingsAsync(ProjectSettings.Default));
	}

	private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private async Task SyncedAsync()
	{
		await _engine.InitializeAsync();
		await _engine.OpenFolderAsync();
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	/// <summary>"a.txt" synced as "v1", then edited here and on the server.</summary>
	private async Task ConflictAsync()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		_folder.Write("a.txt", "mine, edited here");
		await EditMirrorAsync("a.txt", "the server's edit");
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(RemoteStatus.Conflict, Assert.Single(_engine.Remote).Status);
	}

	private async Task EditMirrorAsync(string path, string text)
	{
		var file = _server.PathOf(Repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, text);
		await _server.Watcher.RaiseAsync(Repo, [path]);
	}

	private string Text(string path) => Encoding.UTF8.GetString(_folder.Files[path]);
}
