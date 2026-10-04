using System.Text;
using AiChromeProxy.Client.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>The gated save of a new file into the picked folder (<see cref="LoopbackServer"/> + <see cref="FakeFolder"/>).</summary>
public sealed class SyncEngineSaveTests : IDisposable
{
	private const string Repo = "My_Repo";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly SyncEngine _engine;
	private int _calls;

	public SyncEngineSaveTests()
	{
		_engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task Save_WritesCreatesFoldersAndTheNextScanUploadsIt()
	{
		await SyncedAsync();

		var result = await _engine.SaveFileAsync("docs/diagrams/a.mmd", () => Task.FromResult(Encoding.UTF8.GetBytes("classDiagram")));

		Assert.Equal(new TreeActionResult(true), result);
		Assert.Equal("classDiagram", Encoding.UTF8.GetString(_folder.Files["docs/diagrams/a.mmd"]));
		Assert.Contains(_engine.Activity, a => a.Text == "Saved 'docs/diagrams/a.mmd'.");
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal("classDiagram", File.ReadAllText(_server.PathOf(Repo, "docs/diagrams/a.mmd")));
	}

	[Fact]
	public async Task Save_ExistingFile_RefusedAndNotOverwritten()
	{
		_folder.Write("a.mmd", "old");
		await SyncedAsync();
		_calls = 0;

		var result = await _engine.SaveFileAsync("a.mmd", Made);

		Assert.Equal(new TreeActionResult(false, "'a.mmd' already exists here."), result);
		Assert.Equal(0, _calls);
		Assert.Equal("old", Encoding.UTF8.GetString(_folder.Files["a.mmd"]));
	}

	[Fact]
	public async Task Save_ExistingExcludedFileNotInTheScan_Refused()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		_folder.Write(".env", "secret");

		var result = await _engine.SaveFileAsync(".env", () => Task.FromResult(new byte[] { 1 }));

		Assert.Equal("'.env' already exists here.", result.Error);
		Assert.Equal("secret", Encoding.UTF8.GetString(_folder.Files[".env"]));
	}

	[Fact]
	public async Task Save_ExistingFileWhoseCheckFails_ReportsTheError()
	{
		await SyncedAsync();
		_folder.HashFailures.Add("a.mmd");

		var result = await _engine.SaveFileAsync("a.mmd", () => Task.FromResult(new byte[] { 1 }));

		Assert.False(result.Ok);
		Assert.Contains("could not be read", result.Error);
	}

	[Theory]
	[InlineData("../a.mmd")]
	[InlineData("docs/")]
	[InlineData("")]
	public async Task Save_InvalidPath_RefusedBeforeAnythingElse(string path)
	{
		await SyncedAsync();
		_calls = 0;

		var result = await _engine.SaveFileAsync(path, Made);

		Assert.False(result.Ok);
		Assert.NotEmpty(result.Error!);
		Assert.Equal(0, _calls);
		Assert.Empty(_folder.Writes);
	}

	[Fact]
	public async Task Save_ServerChangeWaiting_Refused()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		await EditMirrorAsync("docs/new.mmd", "server");
		_folder.WriteAccess = true;
		_calls = 0;

		var result = await _engine.SaveFileAsync("docs/new.mmd", Made);

		Assert.Equal(SyncEngine.ResolveFirst, result.Error);
		Assert.Equal(0, _calls);
		Assert.False(_folder.Files.ContainsKey("docs/new.mmd"));
	}

	[Fact]
	public async Task Save_FileAppearsWhileTheContentIsMade_RefusedAndNotOverwritten()
	{
		await SyncedAsync();

		var result = await _engine.SaveFileAsync("a.mmd", () =>
		{
			_folder.Write("a.mmd", "theirs");
			return Task.FromResult(new byte[] { 1 });
		});

		Assert.Equal("'a.mmd' already exists here.", result.Error);
		Assert.Equal("theirs", Encoding.UTF8.GetString(_folder.Files["a.mmd"]));
		Assert.Contains(_engine.Activity, a => a.Text == "Could not save 'a.mmd': 'a.mmd' already exists here.");
	}

	[Fact]
	public async Task Save_WriteAccessRefused_StopsBeforeTheContentIsMade()
	{
		await SyncedAsync();
		_folder.ClickFailure = new JSException("NotAllowedError: policy");
		_calls = 0;

		var result = await _engine.SaveFileAsync("a.mmd", Made);

		Assert.Equal(new TreeActionResult(false, "Write access to the folder was not granted; nothing was changed."), result);
		Assert.Equal(0, _calls);
		Assert.False(_folder.Files.ContainsKey("a.mmd"));
	}

	[Fact]
	public async Task Save_AsksForWriteAccessFirst_ThenMakesTheContent()
	{
		await SyncedAsync();

		var result = await _engine.SaveFileAsync("a.mmd", () => Task.FromResult(new byte[] { _folder.WriteAccess ? (byte)1 : (byte)0 }));

		Assert.True(result.Ok);
		Assert.Equal(new byte[] { 1 }, _folder.Files["a.mmd"]);
	}

	[Fact]
	public async Task Save_ContentFails_ReportsTheErrorAndWritesNothing()
	{
		await SyncedAsync();
		_folder.WriteAccess = true;

		var result = await _engine.SaveFileAsync("a.png", () => throw new InvalidOperationException("render failed"));

		Assert.Equal(new TreeActionResult(false, "render failed"), result);
		Assert.False(_folder.Files.ContainsKey("a.png"));
		Assert.Contains(_engine.Activity, a => a.Text == "Could not save 'a.png': render failed");
	}

	[Fact]
	public async Task Save_Excluded_NotesItAndDoesNotSyncIt()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		var result = await _engine.SaveFileAsync(".env", () => Task.FromResult(new byte[] { 1 }));

		Assert.Equal(new TreeActionResult(true, null, "Saved; excluded from sync"), result);
		Assert.Contains(_engine.Activity, a => a.Text == "Saved '.env'; excluded from sync.");
		await _engine.SyncOnceAsync(Ct);
		Assert.False(File.Exists(_server.PathOf(Repo, ".env")));
	}

	private Task<byte[]> Made()
	{
		_calls++;
		return Task.FromResult(new byte[] { 1 });
	}

	private async Task SyncedAsync()
	{
		await _engine.InitializeAsync();
		await _engine.OpenFolderAsync();
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		_folder.Writes.Clear();
		_server.Transport.Sent.Clear();
	}

	private async Task EditMirrorAsync(string path, string text)
	{
		var file = _server.PathOf(Repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		await File.WriteAllTextAsync(file, text, Ct);
		await _server.Watcher.RaiseAsync(Repo, [path]);
	}
}
