using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Client;

/// <summary>The engine's back channel (server changes written under the hash-guard, conflicts, project settings) against <see cref="LoopbackServer"/>.</summary>
public sealed class SyncEngineBackChannelTests : IDisposable
{
	private const string Repo = "My_Repo";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private SyncEngine _engine;

	public SyncEngineBackChannelTests()
	{
		_engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task ServerEdit_WithWriteAccess_WrittenAndAcked()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		_server.Transport.Sent.Clear();

		await EditMirrorAsync("a.txt", "v2 from the server");
		Assert.Equal(RemoteStatus.Waiting, Assert.Single(_engine.Remote).Status);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("v2 from the server", Text("a.txt"));
		Assert.Empty(_engine.Remote);
		Assert.True(_engine.CanWrite);
		Assert.Equal(Sha("v2 from the server"), _server.Projects.GetBases(Repo)["a.txt"]);
		Assert.Equal([MessageTypes.SyncFetch, MessageTypes.SyncAck], SentTypes());
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("a.txt")!.State);
		Assert.Contains(_engine.Activity, a => a.Kind == SyncActivityKind.Received && a.Text == "Received 1 file from the server: a.txt.");
	}

	[Fact]
	public async Task ServerEdit_WithoutWriteAccess_Waits_ThenAllowWriting()
	{
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "v2 from the server");

		await _engine.SyncOnceAsync(Ct);

		var item = Assert.Single(_engine.Remote);
		Assert.Equal((RemoteStatus.Waiting, "a.txt", Sha("v2 from the server"), Sha("v1")), (item.Status, item.Change.Path, item.Change.Sha256, item.Change.Base));
		Assert.False(_engine.CanWrite);
		Assert.Equal("v1", Text("a.txt"));
		Assert.Empty(_folder.Writes);
		Assert.Equal("v2 from the server", File.ReadAllText(_server.PathOf(Repo, "a.txt")));

		await _engine.AllowWritingAsync();
		Assert.True(_engine.CanWrite);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("v2 from the server", Text("a.txt"));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task ServerDelete_Applied()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await SyncedAsync();
		_server.Transport.Sent.Clear();

		await EditMirrorAsync("a.txt", null);
		await _engine.SyncOnceAsync(Ct);

		Assert.False(_folder.Files.ContainsKey("a.txt"));
		Assert.Equal(["a.txt"], _folder.Writes);
		Assert.False(_server.Projects.GetBases(Repo).ContainsKey("a.txt"));
		Assert.Equal([MessageTypes.SyncAck], SentTypes());
		Assert.Empty(_engine.Remote);
		Assert.Equal(["b.txt"], _engine.Files.Select(f => f.Path));
		Assert.Contains(_engine.Activity, a => a.Text == "Deleted 1 file on the server: a.txt.");
	}

	[Fact]
	public async Task ServerCreate_Applied_FoldersCreated()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		await SyncedAsync();

		await EditMirrorAsync("docs/new/x.md", "# x");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("# x", Text("docs/new/x.md"));
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("docs/new/x.md")!.State);
		Assert.Equal(Sha("# x"), _server.Projects.GetBases(Repo)["docs/new/x.md"]);
	}

	[Fact]
	public async Task AlreadyThere_AckedWithoutWriting()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "same on both");
		_folder.Write("a.txt", "same on both");
		_server.Transport.Sent.Clear();

		await _engine.SyncOnceAsync(Ct);

		Assert.Empty(_folder.Writes);
		Assert.Empty(_engine.Remote);
		Assert.Equal([MessageTypes.SyncAck], SentTypes());
		Assert.Equal(Sha("same on both"), _server.Projects.GetBases(Repo)["a.txt"]);
	}

	[Fact]
	public async Task BothChanged_Conflict_NotUploadedNotWritten()
	{
		await ConflictAsync();

		Assert.Equal(1, _engine.ConflictCount);
		var item = Assert.Single(_engine.Remote);
		Assert.Equal((RemoteStatus.Conflict, Sha("mine, edited here")), (item.Status, item.Local));
		Assert.Equal("the server's edit", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("mine, edited here", Text("a.txt"));
		Assert.Empty(_folder.Writes);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.SyncChunk);

		await _engine.SyncOnceAsync(Ct);

		var conflict = Assert.Single(_engine.Activity, a => a.Kind == SyncActivityKind.Conflict);
		Assert.Equal("Conflict: a.txt changed here and on the server.", conflict.Text);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.SyncChunk);
		Assert.Equal("the server's edit", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task KeepMine_UploadsLocalVersion()
	{
		await ConflictAsync();

		await _engine.KeepMineAsync("a.txt");
		Assert.Empty(_engine.Remote);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("mine, edited here", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal(Sha("mine, edited here"), _server.Projects.GetBases(Repo)["a.txt"]);
		Assert.Equal("mine, edited here", Text("a.txt"));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task TakeServers_WritesServerVersion()
	{
		await ConflictAsync();
		_server.Transport.Sent.Clear();

		await _engine.TakeServersAsync("a.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("the server's edit", Text("a.txt"));
		Assert.Equal("the server's edit", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal(Sha("the server's edit"), _server.Projects.GetBases(Repo)["a.txt"]);
		Assert.Empty(_engine.Remote);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.SyncChunk or MessageTypes.SyncDelta);
	}

	[Fact]
	public async Task ServerDeletedWhileEditedHere_KeepMineUploadsAgain_OrTakeServersDeletes()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.Write("c.txt", "c"); // the mirror keeps a file: an empty one starts over instead
		await SyncedAsync();
		_folder.Write("a.txt", "a, edited here");
		_folder.Write("b.txt", "b, edited here");
		await EditMirrorAsync("a.txt", null);
		await EditMirrorAsync("b.txt", null);
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(2, _engine.ConflictCount);
		Assert.All(_engine.Remote, r => Assert.Null(r.Change.Sha256));

		await _engine.KeepMineAsync("a.txt");
		await _engine.TakeServersAsync("b.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("a, edited here", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.False(_folder.Files.ContainsKey("b.txt"));
		Assert.False(File.Exists(_server.PathOf(Repo, "b.txt")));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task TakeServers_WithoutWriteAccess_AsksFirst()
	{
		await ConflictAsync();
		_folder.WriteAccess = false;

		// The last answer was "granted": the write fails, the item stays and the engine notices the lost permission.
		await _engine.TakeServersAsync("a.txt");
		Assert.False(_engine.CanWrite);
		Assert.Equal(1, _engine.ConflictCount);

		await _engine.TakeServersAsync("a.txt");

		Assert.True(_engine.CanWrite);
		Assert.Equal("the server's edit", Text("a.txt"));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task ApplyServerChangesOff_Waits_ApplyAll_Writes()
	{
		_server.Projects.SaveSettings(Repo, new ProjectSettings { ApplyServerChanges = false });
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.Write("c.txt", "c");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "a from the server");
		await EditMirrorAsync("b.txt", "b from the server");
		await EditMirrorAsync("c.txt", "c from the server");

		await _engine.SyncOnceAsync(Ct);

		Assert.False(_engine.Settings.ApplyServerChangesOrDefault);
		Assert.Equal(["a.txt", "b.txt", "c.txt"], _engine.Remote.Select(r => r.Change.Path));
		Assert.All(_engine.Remote, r => Assert.Equal(RemoteStatus.Waiting, r.Status));
		Assert.Empty(_folder.Writes);

		await _engine.ApplyAsync("b.txt");

		Assert.Equal("b from the server", Text("b.txt"));
		Assert.Equal(["a.txt", "c.txt"], _engine.Remote.Select(r => r.Change.Path));

		await _engine.ApplyAsync(null);

		Assert.Equal("a from the server", Text("a.txt"));
		Assert.Equal("c from the server", Text("c.txt"));
		Assert.Empty(_engine.Remote);
		Assert.Contains(_engine.Activity, a => a.Text == "Received 2 files from the server: a.txt, c.txt.");
	}

	[Fact]
	public async Task IgnoredOrInvalidRemotePath_NeverWritten()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		await SyncedAsync();
		_server.Transport.Sent.Clear();
		var errors = _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error);

		_server.Transport.Push(Remote("Other_Repo", new RemoteChange("x.txt", Sha("x"), 1, null)));
		Assert.Empty(_engine.Remote);
		_server.Transport.Push(Remote(Repo, new RemoteChange(".env", Sha("x"), 1, null), new RemoteChange("a/../b", Sha("x"), 1, null)));
		Assert.Equal(2, _engine.Remote.Count);

		await _engine.SyncOnceAsync(Ct);

		Assert.Empty(_folder.Writes);
		Assert.Empty(_engine.Remote);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.SyncFetch or MessageTypes.SyncAck);
		Assert.Equal(errors + 2, _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error));
	}

	[Fact]
	public async Task GoneOnTheServerOrUnreadableHere_DroppedWithError()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		await SyncedAsync();
		_folder.HashFailures.Add("a.txt");
		var errors = _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error);

		_server.Transport.Push(Remote(Repo, new RemoteChange("a.txt", Sha("x"), 1, Sha("a")), new RemoteChange("ghost.txt", Sha("x"), 1, null)));
		await _engine.SyncOnceAsync(Ct);

		Assert.Empty(_folder.Writes);
		Assert.Empty(_engine.Remote);
		Assert.True(_engine.CanWrite);
		Assert.Equal(errors + 2, _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error && a.Text.StartsWith("Could not apply", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task FetchHashMismatch_Dropped()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "v2 from the server");
		File.WriteAllText(_server.PathOf(Repo, "a.txt"), "v3, edited again on the server");

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("v1", Text("a.txt"));
		Assert.Empty(_folder.Writes);
		Assert.Empty(_engine.Remote);

		await _server.Watcher.RaiseAsync(Repo, ["a.txt"]);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("v3, edited again on the server", Text("a.txt"));
	}

	[Fact]
	public async Task PeriodicFullManifest_AfterServerEdit_DoesNotOverwrite()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		_engine = new SyncEngine(_server.Transport, _folder, clock);
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "v2 from the server");
		_server.Transport.Sent.Clear();

		clock.Advance(SyncEngine.FullManifestInterval);
		await _engine.SyncOnceAsync(Ct);

		Assert.Contains(MessageTypes.SyncManifest, SentTypes());
		Assert.Equal("v2 from the server", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("v1", Text("a.txt"));

		await _engine.AllowWritingAsync();
		clock.Advance(SyncEngine.FullManifestInterval);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("v2 from the server", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("v2 from the server", Text("a.txt"));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task MirrorWiped_FolderKeepsEveryFile_UploadedAgain()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		_engine = new SyncEngine(_server.Transport, _folder, clock);
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		_folder.Write("d/b.txt", "b");
		await SyncedAsync();

		Directory.Delete(Path.Combine(_server.MirrorRoot, Repo), recursive: true);
		await _server.Watcher.RaiseAsync(Repo, ["a.txt", "d"]);
		await _engine.SyncOnceAsync(Ct);
		clock.Advance(SyncEngine.FullManifestInterval);
		await _engine.SyncOnceAsync(Ct);

		Assert.Empty(_folder.Writes);
		Assert.Equal(["a.txt", "d/b.txt"], _folder.Files.Keys.Order(StringComparer.Ordinal));
		Assert.Empty(_engine.Remote);
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("b", File.ReadAllText(_server.PathOf(Repo, "d/b.txt")));
		Assert.True(_server.Projects.IsBaselined(Repo));
	}

	[Fact]
	public async Task UploadRacingServerEdit_NotBackedOff_BecomesConflict()
	{
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		_folder.Write("a.txt", "mine, edited here");
		var real = _server.Transport.Reply!;
		_server.Transport.Reply = e =>
		{
			if (e.Type == MessageTypes.SyncChunk)
			{
				File.WriteAllText(_server.PathOf(Repo, "a.txt"), "the server's edit");
			}

			return real(e);
		};

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(RemoteStatus.Waiting, Assert.Single(_engine.Remote).Status);
		Assert.DoesNotContain(_engine.Errors, e => e.StartsWith("a.txt", StringComparison.Ordinal));
		Assert.DoesNotContain(_engine.Activity, a => a.Text.StartsWith("Could not upload", StringComparison.Ordinal));

		_server.Transport.Reply = real;
		await _engine.AllowWritingAsync();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(1, _engine.ConflictCount);
		Assert.Equal("the server's edit", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("mine, edited here", Text("a.txt"));
	}

	[Fact]
	public async Task SettingsExcludes_AppliedToScan()
	{
		_server.Projects.SaveSettings(Repo, new ProjectSettings { Excludes = "docs/" });
		_folder.Write("a.txt", "a");
		_folder.Write("docs/a.md", "d");

		await SyncedAsync();

		Assert.Equal("docs/", _engine.Settings.Excludes);
		Assert.Equal(["a.txt"], _engine.Files.Select(f => f.Path));
		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "docs/a.md")));

		await _engine.SaveSettingsAsync(new ProjectSettings { ApplyServerChanges = false });
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(new ProjectSettings { ApplyServerChanges = false }, _engine.Settings);
		Assert.Equal(_engine.Settings, _server.Projects.GetSettings(Repo));
		Assert.Equal("d", File.ReadAllText(_server.PathOf(Repo, "docs/a.md")));
	}

	[Fact]
	public async Task OldServer_NoSettings_Defaults()
	{
		var real = _server.Transport.Reply!;
		_server.Transport.Reply = async e =>
		{
			var reply = await real(e);
			return reply?.Type == MessageTypes.SyncOpened ? Envelope.Create(MessageTypes.SyncOpened, new { repo = Repo }, reply.CorrelationId) : reply;
		};
		_folder.Write("a.txt", "a");

		await SyncedAsync();

		Assert.Same(ProjectSettings.Default, _engine.Settings);
	}

	[Fact]
	public async Task ServerText_SmallUtf8_ElseNull()
	{
		_folder.Write("a.txt", "a");
		Assert.Null(await _engine.ServerTextAsync("a.txt"));
		await SyncedAsync();
		File.WriteAllText(_server.PathOf(Repo, "t.md"), "héllo");
		File.WriteAllBytes(_server.PathOf(Repo, "b.bin"), [0xFF, 0xFE, 0x00]);
		var boundary = new string('x', SyncLimits.ChunkSize * 2);
		File.WriteAllText(_server.PathOf(Repo, "boundary.txt"), boundary);
		File.WriteAllText(_server.PathOf(Repo, "big.txt"), new string('x', (256 * 1024) + 1));

		Assert.Equal("héllo", await _engine.ServerTextAsync("t.md"));
		Assert.Equal(boundary, await _engine.ServerTextAsync("boundary.txt"));
		Assert.Null(await _engine.ServerTextAsync("b.bin"));
		Assert.Null(await _engine.ServerTextAsync("big.txt"));
		Assert.Null(await _engine.ServerTextAsync("missing.txt"));
	}

	[Fact]
	public async Task FolderChange_ForgetsServerChanges()
	{
		await ConflictAsync();

		await _engine.OpenFolderAsync();

		Assert.Empty(_engine.Remote);
		Assert.Equal(0, _engine.ConflictCount);
	}

	[Fact]
	public async Task SavedHereDuringTheFetch_Conflict_NotOverwritten()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "v2 from the server");
		var real = _server.Transport.Reply!;
		_server.Transport.Reply = e =>
		{
			if (e.Type == MessageTypes.SyncFetch)
			{
				_folder.Write("a.txt", "saved here during the fetch");
			}

			return real(e);
		};

		await _engine.SyncOnceAsync(Ct);

		var item = Assert.Single(_engine.Remote);
		Assert.Equal((RemoteStatus.Conflict, Sha("saved here during the fetch")), (item.Status, item.Local));
		Assert.Equal("saved here during the fetch", Text("a.txt"));
		Assert.Empty(_folder.Writes);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.SyncAck);
	}

	[Fact]
	public async Task KeepMineAndTakeServers_OnlyForConflicts()
	{
		_folder.Write("a.txt", "a");
		await SyncedAsync();
		_server.Transport.Push(Remote(Repo, new RemoteChange(".env", Sha("x"), 1, null), new RemoteChange("a.txt", Sha("x"), 1, Sha("a"))));

		await _engine.KeepMineAsync(".env");
		await _engine.TakeServersAsync(".env");
		await _engine.KeepMineAsync("a.txt");
		await _engine.TakeServersAsync("a.txt");
		await _engine.KeepMineAsync("missing.txt");
		await _engine.TakeServersAsync("missing.txt");

		Assert.Equal([".env", "a.txt"], _engine.Remote.Select(r => r.Change.Path));
		Assert.All(_engine.Remote, r => Assert.Equal(RemoteStatus.Waiting, r.Status));
		Assert.False(_engine.CanWrite);
		Assert.Empty(_folder.Writes);
		Assert.Empty(_server.Transport.Sent);
	}

	[Fact]
	public async Task ConflictExcludedBySettings_ResolveDropsIt_NothingWrittenOrAcked()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.Write("c.md", "c");
		await SyncedAsync();
		_folder.Write("a.txt", "a, edited here");
		_folder.Write("b.txt", "b, edited here");
		await EditMirrorAsync("a.txt", "a from the server");
		await EditMirrorAsync("b.txt", "b from the server");
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(2, _engine.ConflictCount);
		await _engine.SaveSettingsAsync(new ProjectSettings { Excludes = "*.txt" });
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();
		var errors = _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error);

		await _engine.KeepMineAsync("a.txt");
		await _engine.TakeServersAsync("b.txt");

		Assert.Empty(_engine.Remote);
		Assert.Empty(_folder.Writes);
		Assert.Equal(("a, edited here", "b, edited here"), (Text("a.txt"), Text("b.txt")));
		Assert.Empty(_server.Transport.Sent);
		Assert.Equal(errors + 2, _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error && a.Text.StartsWith("Not written", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task ConflictDeletedHere_NoDeleteSent_UntilKeepMine()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await SyncedAsync();
		_folder.Files.Remove("a.txt");
		await EditMirrorAsync("a.txt", "a from the server");

		await _engine.SyncOnceAsync(Ct);
		await _engine.SyncOnceAsync(Ct);

		var item = Assert.Single(_engine.Remote);
		Assert.Equal((RemoteStatus.Conflict, null), (item.Status, item.Local));
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.SyncDelta);
		Assert.Equal("a from the server", File.ReadAllText(_server.PathOf(Repo, "a.txt")));

		await _engine.KeepMineAsync("a.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task WriteAccessLostWhileApplying_RestKeepsWaiting()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "a from the server");
		await EditMirrorAsync("b.txt", "b from the server");
		var errors = _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error);

		// "Granted" when the cycle checks, gone when the first write is made.
		_folder.WriteAccessAnswers.Enqueue(true);
		await _engine.SyncOnceAsync(Ct);

		Assert.False(_engine.CanWrite);
		Assert.Equal(["a.txt", "b.txt"], _engine.Remote.Select(r => r.Change.Path));
		Assert.All(_engine.Remote, r => Assert.Equal(RemoteStatus.Waiting, r.Status));
		Assert.Empty(_folder.Writes);
		Assert.Equal(errors, _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error));

		await _engine.ApplyAsync(null);
		Assert.Contains(_engine.Activity, a => a.Text == "Server changes wait: allow writing to the folder first.");

		await _engine.AllowWritingAsync();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(("a from the server", "b from the server"), (Text("a.txt"), Text("b.txt")));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task AckRefusedAfterWrite_TreatedAsWritten_DistinctMessage()
	{
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "v1");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "v2 from the server");
		var real = _server.Transport.Reply!;
		_server.Transport.Reply = e => e.Type == MessageTypes.SyncAck
			? Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Error, new ErrorPayload(ErrorCodes.Internal, "Database is locked."), e.CorrelationId))
			: real(e);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("v2 from the server", Text("a.txt"));
		Assert.Empty(_engine.Remote);
		Assert.Contains(_engine.Activity, a => a.Text == "Wrote a.txt, but the server did not take the confirmation: Database is locked.");
		Assert.DoesNotContain(_engine.Activity, a => a.Text.StartsWith("Could not apply", StringComparison.Ordinal));
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.SyncDelta or MessageTypes.SyncChunk);
	}

	private static Envelope Remote(string repo, params RemoteChange[] changes) =>
		Envelope.Create(MessageTypes.SyncRemote, new SyncRemotePayload(repo, changes));

	private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	/// <summary>Picks the folder, syncs once (the repo is baselined) and listens to pushes; forgets what was sent and written so far.</summary>
	private async Task SyncedAsync()
	{
		await _engine.InitializeAsync();
		await _engine.OpenFolderAsync();
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.True(_server.Projects.IsBaselined(Repo));
		_folder.Writes.Clear();
		_server.Transport.Sent.Clear();
	}

	/// <summary>"a.txt" synced as "v1", then edited here and on the server: a conflict once write access is granted.</summary>
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

	/// <summary>Writes (or, with null, deletes) a mirror file and raises the server's watcher for it: the push reaches the engine.</summary>
	private async Task EditMirrorAsync(string path, string? text)
	{
		var file = _server.PathOf(Repo, path);
		if (text is null)
		{
			File.Delete(file);
		}
		else
		{
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, text);
		}

		await _server.Watcher.RaiseAsync(Repo, [path]);
	}

	private string Text(string path) => Encoding.UTF8.GetString(_folder.Files[path]);

	private List<string> SentTypes() => [.. _server.Transport.Sent.Select(e => e.Type)];
}
