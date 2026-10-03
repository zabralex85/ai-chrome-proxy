using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>The browser-side engine against the real server-side sync (<see cref="LoopbackServer"/>) and an in-memory folder.</summary>
public sealed class SyncEngineTests : IDisposable
{
	private const string Repo = "My_Repo";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly SyncEngine _engine;
	private Func<Envelope, Task<Envelope?>>? _realReply;

	public SyncEngineTests()
	{
		_engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task FirstSync_OpenManifestUpload_MirrorHasIncludedFilesOnly()
	{
		_folder.Write("README.md", "# hi");
		_folder.Write("src/app.cs", "class App { }");
		_folder.Write(".gitignore", "build/\n*.log\n");
		_folder.Write(".env", "SECRET=1");
		_folder.Write("certs/site.pem", "key");
		_folder.Write("node_modules/x/index.js", "x");
		_folder.Write("src/bin/app.dll", "dll");
		_folder.Write("build/out.js", "out");
		_folder.Write("debug.log", "log");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Null(_engine.Problem);
		Assert.Equal([".gitignore", "README.md", "src/app.cs"], _engine.Files.Select(f => f.Path));
		Assert.All(_engine.Files, f => Assert.Equal(FileSyncState.Synced, f.State));
		Assert.Equal([".gitignore", "README.md", "src/app.cs"], _folder.Hashed.Order(StringComparer.Ordinal));
		Assert.Equal("class App { }", File.ReadAllText(_server.PathOf(Repo, "src/app.cs")));
		Assert.False(File.Exists(_server.PathOf(Repo, ".env")));
		Assert.False(Directory.Exists(_server.PathOf(Repo, "node_modules")));
		Assert.False(Directory.Exists(_server.PathOf(Repo, "build")));
		Assert.Equal([MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncChunk, MessageTypes.SyncChunk, MessageTypes.SyncChunk], SentTypes());
		Assert.Equal(3, _engine.UploadTotal);
		Assert.Equal(3, _engine.UploadDone);
		Assert.NotNull(_engine.LastSync);
	}

	[Fact]
	public async Task GitignoredFolders_NotWalked_FromTheFirstScan()
	{
		_folder.Write(".gitignore", "build/\n/dist\n*.log\n");
		_folder.Write("a.txt", "a");
		_folder.Write("build/out.js", "o");
		_folder.Write("src/build/out.js", "o");
		_folder.Write("dist/x.js", "x");
		_folder.Write("src/dist/y.js", "y");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal([.. IgnoreRules.BuiltInDirectories, "build", "/dist"], _folder.SkippedDirectories);
		Assert.Equal([".gitignore", "a.txt", "src/dist/y.js"], _engine.Files.Select(f => f.Path));
		Assert.True(File.Exists(_server.PathOf(Repo, "src/dist/y.js")));
	}

	[Fact]
	public async Task LargeFile_SentInChunks()
	{
		var content = Enumerable.Range(0, (SyncLimits.ChunkSize * 2) + 5).Select(i => (byte)i).ToArray();
		_folder.Files["big.bin"] = content;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(content, File.ReadAllBytes(_server.PathOf(Repo, "big.bin")));
		Assert.Equal(3, SentTypes().Count(t => t == MessageTypes.SyncChunk));
	}

	[Fact]
	public async Task ChunkData_IsBase64UrlWithoutPadding()
	{
		byte[] content = [0xFB, 0xFF, 0xBF, 0xFE];
		_folder.Files["bytes.bin"] = content;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		var chunk = _server.Transport.Sent.Single(e => e.Type == MessageTypes.SyncChunk).Payload.Deserialize<SyncChunkPayload>(JsonSerializerOptions.Web)!;
		Assert.Equal(SyncData.Encode(content), chunk.Data);
		Assert.True(chunk.Data.IndexOfAny(['+', '/', '=']) < 0, chunk.Data);
		Assert.Equal(content, File.ReadAllBytes(_server.PathOf(Repo, "bytes.bin")));
	}

	[Fact]
	public async Task NoChanges_SecondScanSendsNothing()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		var sent = _server.Transport.Sent.Count;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(sent, _server.Transport.Sent.Count);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task Files_SameListWhileNothingChanges_FileAtFindsEntries()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		var files = _engine.Files;

		await _engine.SyncOnceAsync(Ct);

		Assert.Same(files, _engine.Files);
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("a.txt")!.State);
		Assert.Null(_engine.FileAt("missing.txt"));

		_folder.Write("b.txt", "b");
		await _engine.SyncOnceAsync(Ct);

		Assert.NotSame(files, _engine.Files);
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("b.txt")!.State);
	}

	[Fact]
	public async Task Changes_SentAsDelta_MirrorFollowsEditAddDeleteRename()
	{
		_folder.Write("edit.txt", "v1");
		_folder.Write("delete.txt", "d");
		_folder.Write("old-name.txt", "r");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();

		_folder.Write("edit.txt", "v2");
		_folder.Write("dir/added.txt", "new");
		_folder.Files.Remove("delete.txt");
		_folder.Files["new-name.txt"] = _folder.Files["old-name.txt"];
		_folder.Files.Remove("old-name.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.DoesNotContain(MessageTypes.SyncOpen, SentTypes());
		Assert.DoesNotContain(MessageTypes.SyncManifest, SentTypes());
		Assert.Contains(MessageTypes.SyncDelta, SentTypes());
		AssertNoDeltaUpsertsAndDeletesTheSamePath();
		Assert.Equal("v2", File.ReadAllText(_server.PathOf(Repo, "edit.txt")));
		Assert.Equal("new", File.ReadAllText(_server.PathOf(Repo, "dir/added.txt")));
		Assert.Equal("r", File.ReadAllText(_server.PathOf(Repo, "new-name.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "delete.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "old-name.txt")));
	}

	[Fact]
	public async Task CaseOnlyRename_NeverUpsertsAndDeletesTheSamePath_MirrorKeepsOldCasing()
	{
		_folder.Write("readme.md", "r");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();

		_folder.Files["README.md"] = _folder.Files["readme.md"];
		_folder.Files.Remove("readme.md");
		await _engine.SyncOnceAsync(Ct);

		Assert.Contains(MessageTypes.SyncDelta, SentTypes());
		AssertNoDeltaUpsertsAndDeletesTheSamePath();
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Empty(_engine.Errors);

		// Known limitation: the case-insensitive mirror already has the content, so the file keeps its old name there.
		Assert.Equal(["readme.md"], Directory.GetFiles(Path.Combine(_server.MirrorRoot, Repo)).Select(Path.GetFileName));
	}

	[Fact]
	public async Task FileOverLimit_ListedTooLarge_NotHashedNotSent()
	{
		_folder.Write("small.txt", "s");
		_folder.SizeOnly["video.mp4"] = SyncLimits.MaxFileSize + 1;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.TooLarge, _engine.Files.Single(f => f.Path == "video.mp4").State);
		Assert.DoesNotContain("video.mp4", _folder.Hashed);
		Assert.False(File.Exists(_server.PathOf(Repo, "video.mp4")));
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task NameNotAllowed_ListedAsError_OthersSynced()
	{
		_folder.Write("ok.txt", "ok");
		_folder.Write("trailing.", "x");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.Error, _engine.Files.Single(f => f.Path == "trailing.").State);
		Assert.Single(_engine.Errors);
		Assert.True(File.Exists(_server.PathOf(Repo, "ok.txt")));
	}

	[Fact]
	public async Task InvalidPath_ShownWithValidatorMessage_NeverHashedSentOrRetried()
	{
		const string ShortName = "GIT~1/hooks/post-checkout";
		_folder.Write("ok.txt", "ok");
		_folder.Write(ShortName, "x");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);
		await _engine.SyncOnceAsync(Ct);

		var file = _engine.FileAt(ShortName)!;
		Assert.Equal(FileSyncState.Error, file.State);
		Assert.Equal(SyncPath.GetError(ShortName), file.Error);
		Assert.Equal([$"{ShortName}: {file.Error}"], _engine.Errors);
		Assert.DoesNotContain(ShortName, _folder.Hashed);
		Assert.DoesNotContain(ShortName, _folder.ChunkReads);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Payload.GetRawText().Contains("hooks/post-checkout", StringComparison.Ordinal));
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.True(File.Exists(_server.PathOf(Repo, "ok.txt")));
	}

	[Fact]
	public async Task MoreThanMaxFiles_Refused_NothingSent()
	{
		for (var i = 0; i <= SyncLimits.MaxFiles; i++)
		{
			_folder.Files[$"f/{i}.txt"] = [];
		}

		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("more than 20,000 files to sync", _engine.Problem, StringComparison.Ordinal);
		Assert.Empty(_server.Transport.Sent);
		Assert.Empty(_folder.Hashed);
	}

	[Fact]
	public async Task WalkTruncated_Refused()
	{
		_folder.Truncated = true;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("pick a smaller folder", _engine.Problem, StringComparison.Ordinal);
		Assert.Empty(_server.Transport.Sent);
	}

	[Fact]
	public async Task ScanInProgress_SecondCycleSkipped()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_folder.ScanGate = gate.Task;

		var first = _engine.SyncOnceAsync(Ct);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(1, _folder.Scans);
		Assert.Equal(SyncPhase.Scanning, _engine.Phase);

		gate.SetResult();
		await first;

		Assert.Equal(1, _folder.Scans);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Single(SentTypes(), MessageTypes.SyncOpen);
	}

	[Fact]
	public async Task Reconnect_NextScanReopensAndSendsFullManifest()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.InitializeAsync();
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();

		_server.Reconnect();
		_folder.Write("a.txt", "changed");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal([MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncChunk], SentTypes());
		Assert.Equal("changed", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task ServerLostSession_CycleFails_NextCycleStartsOver()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		_server.DropSession();
		_folder.Write("b.txt", "b");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Equal("Send sync.open first.", _engine.Problem);
		Assert.Equal(["Send sync.open first."], _engine.Errors);
		Assert.False(_engine.Blocked);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Null(_engine.Problem);
		Assert.Equal("b", File.ReadAllText(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task DeltaRefused_ShownOnce_NextCycleStartsOverWithFullManifest()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		var refusals = 0;
		RefuseWhen(e => e.Type == MessageTypes.SyncDelta && refusals++ == 0, ErrorCodes.TooLarge, "At most 20000 files may wait for upload.");

		_folder.Write("b.txt", "b");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Equal("At most 20000 files may wait for upload.", _engine.Problem);
		Assert.False(_engine.Blocked);
		_server.Transport.Sent.Clear();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal([MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncChunk], SentTypes());
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal("b", File.ReadAllText(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task ManifestRefused_Blocked_NotRetried_ChangeFolderResumes()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		RefuseWhen(e => e.Type == MessageTypes.SyncManifest, ErrorCodes.TooLarge, "At most 20000 files may wait for upload.");

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.True(_engine.Blocked);
		Assert.Contains("At most 20000 files may wait for upload.", _engine.Problem, StringComparison.Ordinal);
		var scans = _folder.Scans;
		_server.Transport.Sent.Clear();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(scans, _folder.Scans);
		Assert.Empty(_server.Transport.Sent);
		Assert.True(_engine.Blocked);

		RefuseWhen(_ => false, ErrorCodes.TooLarge, string.Empty);
		await _engine.OpenFolderAsync();
		await _engine.SyncOnceAsync(Ct);

		Assert.False(_engine.Blocked);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task EmptyFolderOverFullMirror_AfterReload_ServerRefuses_BlockedUntilRestoreAccess()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		// A reloaded page knows nothing about the mirror, so only the server's guard stands between an empty folder and a wipe.
		var engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		await engine.OpenFolderAsync();
		_folder.Files.Clear();
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, engine.Phase);
		Assert.True(engine.Blocked);
		Assert.Contains("An empty folder would delete the whole mirror", engine.Problem, StringComparison.Ordinal);
		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
		_server.Transport.Sent.Clear();

		await engine.SyncOnceAsync(Ct);

		Assert.Empty(_server.Transport.Sent);

		_folder.Write("a.txt", "back");
		await engine.RestoreAccessAsync();

		Assert.False(engine.Blocked);

		await engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, engine.Phase);
		Assert.Equal("back", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task FolderLooksEmpty_WhileFilesAreKnown_PassRefused_NothingDeleted()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.InitializeAsync();
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();
		_folder.Files.Clear();

		await _engine.SyncOnceAsync(Ct);
		_server.Reconnect();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Equal("The folder looks empty; nothing was deleted. Check access or pick the folder again.", _engine.Problem);
		Assert.Empty(_server.Transport.Sent);
		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.True(File.Exists(_server.PathOf(Repo, "b.txt")));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task KnownFileTurnsUnreadable_NeitherDeletedNorUploaded_DeltaOrFullManifest(bool skippedByWalk)
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.InitializeAsync();
		await _engine.SyncOnceAsync(Ct);
		_folder.ChunkReads.Clear();
		(skippedByWalk ? _folder.Unreadable : _folder.HashFailures).Add("a.txt");
		_folder.Write("b.txt", "b2");

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal(FileSyncState.Error, _engine.FileAt("a.txt")!.State);
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("b2", File.ReadAllText(_server.PathOf(Repo, "b.txt")));

		_server.Reconnect();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		var manifest = LastManifest();
		Assert.Equal(["a.txt"], manifest.Keep);
		Assert.DoesNotContain(manifest.Entries, e => e.Path == "a.txt");
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.DoesNotContain("a.txt", _folder.ChunkReads);
		AssertNoDeltaDeletes();
	}

	[Fact]
	public async Task UnlistedFolder_ItsKnownFilesNeitherDeletedNorUploaded()
	{
		_folder.Write("ok.txt", "ok");
		_folder.Write("locked/x.txt", "x");
		await OpenAsync();
		await _engine.InitializeAsync();
		await _engine.SyncOnceAsync(Ct);
		_folder.UnlistedDirectories.Add("locked");

		await _engine.SyncOnceAsync(Ct);
		_server.Reconnect();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal("x", File.ReadAllText(_server.PathOf(Repo, "locked/x.txt")));
		Assert.Equal(FileSyncState.Error, _engine.FileAt("locked/x.txt")!.State);
		Assert.Contains(_engine.Errors, e => e.StartsWith("locked/: ", StringComparison.Ordinal));
		AssertNoDeltaDeletes();
	}

	[Fact]
	public async Task PartlyListedFolder_ItsListedFilesSyncOnce_TheRestIsKept()
	{
		_folder.Write("ok.txt", "ok");
		_folder.Write("part/a.txt", "a");
		_folder.Write("part/gone.txt", "g");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		_folder.PartlyListedDirectories.Add("part");
		_folder.Files.Remove("part/gone.txt");
		_folder.Write("part/a.txt", "a2");
		await _engine.SyncOnceAsync(Ct);
		var sent = _server.Transport.Sent.Count;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(sent, _server.Transport.Sent.Count);
		Assert.Equal("a2", File.ReadAllText(_server.PathOf(Repo, "part/a.txt")));
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("part/a.txt")!.State);
		Assert.True(File.Exists(_server.PathOf(Repo, "part/gone.txt")));
		AssertNoDeltaDeletes();
	}

	[Fact]
	public async Task UnreadableUnknownFile_AfterReload_FullManifestKeepsIt()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		var engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		await engine.OpenFolderAsync();
		_folder.Unreadable.Add("a.txt");
		_folder.Write("c.txt", "c");
		_server.Transport.Sent.Clear();
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, engine.Phase);
		var manifest = LastManifest();
		Assert.Equal(["a.txt"], manifest.Keep);
		Assert.DoesNotContain(manifest.Entries, e => e.Path == "a.txt");
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("c", File.ReadAllText(_server.PathOf(Repo, "c.txt")));
		Assert.DoesNotContain("a.txt", _folder.ChunkReads.Skip(2));
	}

	[Fact]
	public async Task ReloadPartial_DeletedWhileClosed_DeletedAtOnce_UnreadableKeptUntilItIsDeleted()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.Write("c.txt", "c");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		// The page is reloaded; meanwhile c.txt was deleted and a.txt cannot be read.
		var engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		await engine.OpenFolderAsync();
		_folder.Files.Remove("c.txt");
		_folder.Unreadable.Add("a.txt");
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, engine.Phase);
		Assert.False(File.Exists(_server.PathOf(Repo, "c.txt")));
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("b", File.ReadAllText(_server.PathOf(Repo, "b.txt")));

		_folder.Unreadable.Clear();
		_folder.Files.Remove("a.txt");
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, engine.Phase);
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.True(File.Exists(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task FailedUploadThenDeleted_DeletedOnMirror()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		_folder.Write("a.txt", "a2");
		_folder.ReadFailures.Add("a.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.Error, _engine.FileAt("a.txt")!.State);

		_folder.Files.Remove("a.txt");
		_folder.ReadFailures.Clear();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.True(File.Exists(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task TooLargeAfterReload_MirrorCopyKept_DeletedWhenTheFileIs()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		var engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		await engine.OpenFolderAsync();
		_folder.Files.Remove("a.txt");
		_folder.SizeOnly["a.txt"] = SyncLimits.MaxFileSize + 1;
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, engine.Phase);
		Assert.Equal(FileSyncState.TooLarge, engine.FileAt("a.txt")!.State);
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));

		_folder.SizeOnly.Clear();
		await engine.SyncOnceAsync(Ct);

		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.True(File.Exists(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task PeriodicFullManifest_NothingToDo_AddsNoHistory()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		var engine = new SyncEngine(_server.Transport, _folder, clock);
		_folder.Write("a.txt", "a");
		await engine.OpenFolderAsync();
		await engine.SyncOnceAsync(Ct);
		var before = engine.Activity.Count;

		clock.Advance(SyncEngine.FullManifestInterval);
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(before, engine.Activity.Count);
	}

	[Fact]
	public async Task OnlyKeptFilesLeft_KnownFileDeleted_NoEmptyFolderError()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		_folder.Write("b.txt", "b");
		_folder.Unreadable.Add("b.txt");
		_folder.Files.Remove("a.txt");

		await _engine.SyncOnceAsync(Ct);

		Assert.DoesNotContain(_engine.Activity, a => a.Kind == SyncActivityKind.Error);
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task FullManifestEveryTenMinutes_RemovesWhatNoDeltaCouldSee()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		var engine = new SyncEngine(_server.Transport, _folder, clock);
		_folder.Write("a.txt", "a");
		await engine.OpenFolderAsync();
		await engine.SyncOnceAsync(Ct);
		File.WriteAllText(_server.PathOf(Repo, "stray.txt"), "s");
		_server.Transport.Sent.Clear();

		clock.Advance(SyncEngine.FullManifestInterval - TimeSpan.FromSeconds(1));
		await engine.SyncOnceAsync(Ct);

		Assert.Empty(_server.Transport.Sent);
		Assert.True(File.Exists(_server.PathOf(Repo, "stray.txt")));

		clock.Advance(TimeSpan.FromSeconds(1));
		await engine.SyncOnceAsync(Ct);

		Assert.Equal([MessageTypes.SyncManifest], SentTypes());
		Assert.False(File.Exists(_server.PathOf(Repo, "stray.txt")));
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task KnownFileGrewPastLimit_MarkedTooLarge_NotDeleted()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.InitializeAsync();
		await _engine.SyncOnceAsync(Ct);
		_folder.Files.Remove("a.txt");
		_folder.SizeOnly["a.txt"] = SyncLimits.MaxFileSize + 1;

		await _engine.SyncOnceAsync(Ct);
		_server.Reconnect();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal(FileSyncState.TooLarge, _engine.FileAt("a.txt")!.State);
		Assert.DoesNotContain("a.txt", _folder.Hashed.Skip(2));
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		AssertNoDeltaDeletes();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnreadableGitignore_PassAborted_NothingSent(bool skippedByWalk)
	{
		_folder.Write(".gitignore", "secret.txt\n");
		_folder.Write("secret.txt", "s");
		(skippedByWalk ? _folder.Unreadable : _folder.HashFailures).Add(".gitignore");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains(".gitignore could not be read", _engine.Problem, StringComparison.Ordinal);
		Assert.Empty(_server.Transport.Sent);
		Assert.Empty(_folder.Hashed);
	}

	[Fact]
	public async Task ReconnectDuringUploads_RemainingFilesNotAttempted_NextPassResends()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.Write("c.txt", "c");
		await OpenAsync();
		await _engine.InitializeAsync();
		var real = _server.Transport.Reply!;
		var reconnected = false;
		_server.Transport.Reply = async e =>
		{
			var reply = await real(e);
			if (reply?.Type == MessageTypes.SyncStored && !reconnected)
			{
				reconnected = true;
				_server.Reconnect();
			}

			return reply;
		};

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(["a.txt"], _folder.ChunkReads);
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.NotNull(_engine.Problem);
		Assert.False(_engine.Blocked);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal(["a.txt", "b.txt", "c.txt"], _folder.ChunkReads);
		Assert.Equal("c", File.ReadAllText(_server.PathOf(Repo, "c.txt")));
	}

	[Fact]
	public async Task Uploads_Pipelined_ManyRepliesAwaitedAtOnce_FailuresStillPerFile()
	{
		var delay = TimeSpan.FromMilliseconds(50);
		WriteFiles(64);
		await OpenAsync();
		RefuseWhen(e => e.Type == MessageTypes.SyncChunk && Read<SyncChunkPayload>(e).Path == "f10.txt", ErrorCodes.TooLarge, "Refused.");
		_server.Transport.ReplyDelay = delay;
		var watch = new Stopwatch();
		_engine.Changed += () =>
		{
			if (_engine.Phase == SyncPhase.Uploading && !watch.IsRunning)
			{
				watch.Start();
			}
		};

		await _engine.SyncOnceAsync(Ct);

		watch.Stop();
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal((64, 64, 63), (_engine.UploadTotal, _engine.UploadDone, _engine.SyncedCount));
		Assert.Equal(FileSyncState.Error, _engine.FileAt("f10.txt")!.State);
		Assert.Equal("Refused.", _engine.FileAt("f10.txt")!.Error);
		Assert.Contains(_engine.Activity, a => a.Text == "Could not upload 1 file: f10.txt: Refused.");
		Assert.All(Enumerable.Range(0, 64).Where(i => i != 10), i => Assert.Equal($"file {i}", File.ReadAllText(_server.PathOf(Repo, $"f{i:00}.txt"))));

		// The uploads: one file at a time waits a round trip per file (64 × 50 ms); pipelined, up to MaxUploadsInFlight replies are awaited together.
		Assert.InRange(_server.Transport.MaxPendingReplies, 8, SyncEngine.MaxUploadsInFlight);
		// Generous for slow CI runners: still well below the 64 round trips one file at a time would take.
		Assert.True(watch.Elapsed < 50 * delay, $"The pass took {watch.Elapsed}.");
	}

	[Fact]
	public async Task ConnectionDropsWhileRepliesPending_PassFailsAtOnce_NothingCountedTwice()
	{
		WriteFiles(64);
		await OpenAsync();
		await _engine.InitializeAsync();
		var real = _server.Transport.Reply!;
		var stored = 0;
		_server.Transport.Reply = async e =>
		{
			var reply = await real(e);
			if (reply?.Type == MessageTypes.SyncStored && ++stored == 64)
			{
				// The server stored every file, but the replies still on their way are lost with the connection.
				_server.Reconnect();
			}

			return reply;
		};
		_server.Transport.ReplyDelay = TimeSpan.FromMilliseconds(50);
		var watch = Stopwatch.StartNew();

		await _engine.SyncOnceAsync(Ct);

		Assert.True(watch.Elapsed < SyncEngine.RequestTimeout / 3, $"The pass took {watch.Elapsed}; it waited for lost replies.");
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("connection was lost", _engine.Problem, StringComparison.Ordinal);
		Assert.InRange(_engine.UploadDone, 1, 63);
		Assert.Equal(_engine.UploadDone, _engine.SyncedCount);
		Assert.DoesNotContain(_engine.Activity, a => a.Kind == SyncActivityKind.PassFinished);

		await _engine.SyncOnceAsync(Ct);

		// The server has every file: none is sent again.
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal(64, _engine.SyncedCount);
		Assert.Equal(0, _engine.UploadTotal);
		Assert.Equal(64, stored);
		Assert.Equal("Already in sync (64 files).", _engine.Activity[0].Text);
	}

	[Fact]
	public async Task FolderChangedWhileOpening_OldCycleStops_NewFolderSyncedUnderItsName()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		var real = _server.Transport.Reply!;
		_server.Transport.Reply = async e =>
		{
			if (e.Type == MessageTypes.SyncOpen && _folder.PickResult == "My Repo")
			{
				_folder.PickResult = "Other";
				await _engine.OpenFolderAsync();
			}

			return await real(e);
		};

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal([MessageTypes.SyncOpen], SentTypes());
		Assert.Null(_engine.Problem);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal("Other", _server.Transport.Sent[1].Payload.Deserialize<SyncOpenPayload>(JsonSerializerOptions.Web)!.Repo);
		Assert.True(File.Exists(_server.PathOf("Other", "a.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task ManifestFailsWithInternalError_NotBlocked_RetriedNextPass()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		var refusals = 0;
		RefuseWhen(e => e.Type == MessageTypes.SyncManifest && refusals++ == 0, ErrorCodes.Internal, "Server is restarting.");

		await _engine.SyncOnceAsync(Ct);

		Assert.False(_engine.Blocked);
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Equal("Server is restarting.", _engine.Problem);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task UploadFailsWithInternalError_NotBackedOff_RetriedNextPass()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		var refusals = 0;
		RefuseWhen(e => e.Type == MessageTypes.SyncChunk && refusals++ == 0, ErrorCodes.Internal, "The file is in use.");

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.Error, _engine.FileAt("a.txt")!.State);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("a.txt")!.State);
	}

	[Fact]
	public async Task PersistentUploadFailure_BackedOffFiveMinutes_LoggedOnce()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		var engine = new SyncEngine(_server.Transport, _folder, clock);
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.ReadFailures.Add("a.txt");
		await engine.OpenFolderAsync();

		await engine.SyncOnceAsync(Ct);
		var sent = _server.Transport.Sent.Count;
		clock.Advance(SyncEngine.ScanInterval);
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(["a.txt", "b.txt"], _folder.ChunkReads);
		Assert.Equal(sent, _server.Transport.Sent.Count);
		Assert.Equal(FileSyncState.Error, engine.FileAt("a.txt")!.State);
		Assert.Contains("could not be read", engine.FileAt("a.txt")!.Error, StringComparison.Ordinal);

		clock.Advance(SyncEngine.FailureBackoff);
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(["a.txt", "b.txt", "a.txt"], _folder.ChunkReads);
		Assert.Single(engine.Activity, a => a.Kind == SyncActivityKind.Error);

		_folder.ReadFailures.Clear();
		clock.Advance(SyncEngine.FailureBackoff);
		await engine.SyncOnceAsync(Ct);

		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal(FileSyncState.Synced, engine.FileAt("a.txt")!.State);
	}

	[Fact]
	public async Task Activity_FullSyncWithNothingToUpload_AlreadyInSync()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		var engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
		await engine.OpenFolderAsync();
		await engine.SyncOnceAsync(Ct);

		Assert.Equal("Already in sync (2 files).", engine.Activity[0].Text);
		Assert.Equal(SyncActivityKind.PassFinished, engine.Activity[0].Kind);
	}

	[Fact]
	public async Task Errors_SameListUntilStatesChange()
	{
		_folder.Write("ok.txt", "ok");
		_folder.Write("trailing.", "x");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		var errors = _engine.Errors;

		await _engine.SyncOnceAsync(Ct);

		Assert.Same(errors, _engine.Errors);

		_folder.Write("also.", "y");
		await _engine.SyncOnceAsync(Ct);

		Assert.NotSame(errors, _engine.Errors);
		Assert.Equal(2, _engine.Errors.Count);
	}

	[Fact]
	public async Task FileChangedDuringUpload_MarkedError_OthersSynced_NewContentSentNextScan()
	{
		_folder.Write("a.txt", "aaaa");
		_folder.Write("b.txt", "bbbb");
		_folder.ReadOverride["a.txt"] = Encoding.UTF8.GetBytes("AAAA");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		var failed = _engine.Files.Single(f => f.Path == "a.txt");
		Assert.Equal(FileSyncState.Error, failed.State);
		Assert.Contains("different size or hash", failed.Error, StringComparison.Ordinal);
		Assert.Equal(FileSyncState.Synced, _engine.Files.Single(f => f.Path == "b.txt").State);
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));

		_folder.ReadOverride.Clear();
		_folder.Write("a.txt", "AAAA");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("AAAA", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.All(_engine.Files, f => Assert.Equal(FileSyncState.Synced, f.State));
	}

	[Fact]
	public async Task FileShrankDuringUpload_MarkedError()
	{
		_folder.Write("a.txt", "aaaa");
		_folder.ReadOverride["a.txt"] = Encoding.UTF8.GetBytes("aa");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Contains("changed while it was uploaded", _engine.Files.Single().Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReadChunkFails_FileMarkedError_OthersSynced_RetriedOnceChanged()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.ReadFailures.Add("a.txt");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Null(_engine.Problem);
		var failed = _engine.FileAt("a.txt")!;
		Assert.Equal(FileSyncState.Error, failed.State);
		Assert.Contains("could not be read", failed.Error, StringComparison.Ordinal);
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("b.txt")!.State);
		Assert.Equal(["a.txt", "b.txt"], _folder.ChunkReads);

		_folder.ReadFailures.Clear();
		_folder.Write("a.txt", "a2");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(["a.txt", "b.txt", "a.txt"], _folder.ChunkReads);
		Assert.Equal("a2", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.All(_engine.Files, f => Assert.Equal(FileSyncState.Synced, f.State));
	}

	[Fact]
	public async Task AccessLost_NeedsPermission_RestoreAccessResumes()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		_folder.ScanFailure = new JSException("NotAllowedError");
		_folder.Remembered = true;
		_folder.Granted = false;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.NeedsPermission, _engine.Folder);
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("Restore access", _engine.Problem, StringComparison.Ordinal);

		_folder.ScanFailure = null;
		await _engine.RestoreAccessAsync();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.Ready, _engine.Folder);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task AccessLostDuringUploads_PassEnds_NothingBackedOff_RestoreAccessResumes()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		_folder.Write("c.txt", "c");
		await OpenAsync();
		_folder.ReadFailures.UnionWith(_folder.Files.Keys);
		_folder.Granted = false;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(["a.txt"], _folder.ChunkReads);
		Assert.Equal(FolderStatus.NeedsPermission, _engine.Folder);
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("Restore access", _engine.Problem, StringComparison.Ordinal);
		Assert.Equal(SyncActivityKind.AccessLost, _engine.Activity[0].Kind);

		_folder.ReadFailures.Clear();
		await _engine.RestoreAccessAsync();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal("c", File.ReadAllText(_server.PathOf(Repo, "c.txt")));
	}

	[Fact]
	public async Task AccessLostDecisionIsCarried_QueuedFailureIsNotCheckedAgain_NothingBackedOff()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await OpenAsync();
		_folder.ReadFailures.Add("a.txt");
		_folder.AccessAnswers.Enqueue(false);
		_folder.AccessAnswers.Enqueue(true);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.NeedsPermission, _engine.Folder);
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Equal(["a.txt"], _folder.ChunkReads);
		_folder.ReadFailures.Clear();
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal("a", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task ReadFailsAfterFirstChunk_FileBackedOff_NextStored_NoTempLeft()
	{
		_folder.Files["a.bin"] = new byte[20000];
		_folder.Write("b.txt", "b");
		await OpenAsync();
		_folder.ReadFailuresAt["a.bin"] = 16384;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("b", File.ReadAllText(_server.PathOf(Repo, "b.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "a.bin")));
		Assert.Contains(_engine.Files, f => f.Path == "a.bin" && f.State == FileSyncState.Error);
		Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_server.PathOf(Repo, "b.txt"))!, "*.aicp-tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task ScanFailsWithAccessStillGranted_CycleFails()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		_folder.ScanFailure = new JSException("disk gone");
		_folder.Remembered = true;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.Ready, _engine.Folder);
		Assert.Equal("disk gone", _engine.Problem);
	}

	[Theory]
	[InlineData(false, false, FolderStatus.None)]
	[InlineData(true, false, FolderStatus.NeedsPermission)]
	[InlineData(true, true, FolderStatus.Ready)]
	public async Task Initialize_RestoresRememberedFolder(bool remembered, bool granted, FolderStatus expected)
	{
		_folder.Remembered = remembered;
		_folder.Granted = granted;

		await _engine.InitializeAsync();

		Assert.Equal(expected, _engine.Folder);
		Assert.Equal(remembered ? "My Repo" : null, _engine.FolderName);
		Assert.NotNull(_folder.Visibility);
	}

	[Fact]
	public async Task OpenFolder_Cancelled_NothingChanges()
	{
		_folder.PickResult = null;

		await _engine.OpenFolderAsync();

		Assert.Equal(FolderStatus.None, _engine.Folder);
		Assert.Null(_engine.FolderName);
	}

	[Fact]
	public async Task Run_SyncsAtOnce_WakesOnFocus_StopsOnCancel()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.InitializeAsync();
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var run = Task.Run(() => _engine.RunAsync(cts.Token), Ct);
			await WaitUntilAsync(() => _folder.Scans == 1 && _engine.NextScan is not null);

			_folder.Visibility!(true);
			await WaitUntilAsync(() => _folder.Scans == 2);

			_folder.Visibility!(false);
			await cts.CancelAsync();
			await run;
		}

		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task Run_Disconnected_DoesNotScan()
	{
		await OpenAsync();
		_server.Transport.SetState(TransportState.Reconnecting);
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var run = Task.Run(() => _engine.RunAsync(cts.Token), Ct);
			await WaitUntilAsync(() => _engine.NextScan is not null);
			await cts.CancelAsync();
			await run;
		}

		Assert.Equal(0, _folder.Scans);
	}

	[Fact]
	public async Task Activity_PassLogsStartedUploadedFinished_NoChangeLogsNothing()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		var engine = new SyncEngine(_server.Transport, _folder, clock);
		_folder.Write("a.txt", "aa");
		_folder.Write("b.txt", "bbb");
		await engine.OpenFolderAsync();

		await engine.SyncOnceAsync(Ct);
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(
			[SyncActivityKind.PassFinished, SyncActivityKind.Uploaded, SyncActivityKind.PassStarted, SyncActivityKind.FolderOpened],
			engine.Activity.Select(a => a.Kind));
		Assert.All(engine.Activity, a => Assert.Equal(clock.GetUtcNow(), a.At));
		Assert.Equal("Opened folder 'My Repo'.", engine.Activity[3].Text);
		Assert.Equal("Full sync of 2 files.", engine.Activity[2].Text);
		Assert.Equal("Uploaded 2 files: a.txt, b.txt.", engine.Activity[1].Text);
		Assert.Equal("Uploaded 2 files, 5 bytes in 0.0 s.", engine.Activity[0].Text);
	}

	[Fact]
	public async Task Activity_DeltaLogsDeletesGrouped_UploadErrorsGrouped()
	{
		for (var i = 0; i < 5; i++)
		{
			_folder.Write($"d{i}.txt", "d");
		}

		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		_folder.Files.Clear();
		_folder.Write("x.txt", "x");
		_folder.ReadFailures.Add("x.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(
			[SyncActivityKind.PassFinished, SyncActivityKind.Error, SyncActivityKind.Deleted, SyncActivityKind.PassStarted],
			_engine.Activity.Take(4).Select(a => a.Kind));
		Assert.Equal("Sync of 1 changed and 5 deleted files.", _engine.Activity[3].Text);
		Assert.Equal("Deleted 5 files: d0.txt, d1.txt, d2.txt and 2 more.", _engine.Activity[2].Text);
		Assert.Equal("Could not upload 1 file: x.txt: NotReadableError: 'x.txt' could not be read.", _engine.Activity[1].Text);
	}

	[Fact]
	public async Task Activity_CycleFailureLoggedOnceWithServerMessage()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		RefuseWhen(e => e.Type == MessageTypes.SyncOpen, ErrorCodes.Internal, "Server is restarting.");

		await _engine.SyncOnceAsync(Ct);
		await _engine.SyncOnceAsync(Ct);

		var errors = _engine.Activity.Where(a => a.Kind == SyncActivityKind.Error).ToList();
		Assert.Equal("Server is restarting.", Assert.Single(errors).Text);
	}

	[Fact]
	public async Task Activity_ReconnectAndAccessLostLogged()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.InitializeAsync();

		_server.Reconnect();
		_folder.ScanFailure = new JSException("NotAllowedError");
		_folder.Remembered = true;
		_folder.Granted = false;
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncActivityKind.AccessLost, _engine.Activity[0].Kind);
		Assert.Contains("Restore access", _engine.Activity[0].Text, StringComparison.Ordinal);
		Assert.Equal(SyncActivityKind.Reconnected, _engine.Activity[1].Kind);
	}

	[Fact]
	public async Task Activity_CappedNewestFirst()
	{
		for (var i = 0; i <= SyncEngine.MaxActivity; i++)
		{
			_folder.PickResult = $"f{i}";
			await _engine.OpenFolderAsync();
		}

		Assert.Equal(SyncEngine.MaxActivity, _engine.Activity.Count);
		Assert.Equal($"Opened folder 'f{SyncEngine.MaxActivity}'.", _engine.Activity[0].Text);
		Assert.Equal("Opened folder 'f1'.", _engine.Activity[^1].Text);
	}

	[Fact]
	public async Task Activity_SameSnapshotUntilSomethingIsLogged_IdsUnique()
	{
		await _engine.OpenFolderAsync();
		var snapshot = _engine.Activity;

		Assert.Same(snapshot, _engine.Activity);

		await _engine.OpenFolderAsync();

		Assert.NotSame(snapshot, _engine.Activity);
		Assert.Single(snapshot);
		Assert.Equal(2, _engine.Activity.Count);
		Assert.Equal(2, _engine.Activity.Select(a => a.Id).Distinct().Count());
	}

	[Fact]
	public async Task Aggregates_MatchTheFileList_AfterEveryChange()
	{
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
		var engine = new SyncEngine(_server.Transport, _folder, clock);
		var mismatches = new List<string>();
		engine.Changed += () =>
		{
			if (AggregateMismatch(engine) is { } mismatch)
			{
				mismatches.Add(mismatch);
			}
		};
		_folder.Write("ok.txt", "ok");
		_folder.Write("b.txt", "bbb");
		_folder.Write("trailing.", "x");
		_folder.SizeOnly["video.mp4"] = SyncLimits.MaxFileSize + 1;
		_folder.ReadFailures.Add("b.txt");
		await engine.OpenFolderAsync();

		await engine.SyncOnceAsync(Ct);

		Assert.Equal((1, 2L, 3, 2), (engine.SyncedCount, engine.SyncedBytes, engine.SyncableCount, engine.ErrorCount));

		_folder.ReadFailures.Clear();
		clock.Advance(SyncEngine.FailureBackoff);
		await engine.SyncOnceAsync(Ct);

		Assert.Equal((2, 5L, 3, 1), (engine.SyncedCount, engine.SyncedBytes, engine.SyncableCount, engine.ErrorCount));

		_folder.Files.Remove("ok.txt");
		await engine.SyncOnceAsync(Ct);

		Assert.Equal((1, 3L, 2, 1), (engine.SyncedCount, engine.SyncedBytes, engine.SyncableCount, engine.ErrorCount));

		RefuseWhen(e => e.Type == MessageTypes.SyncDelta, ErrorCodes.Internal, "Server is restarting.");
		_folder.Write("c.txt", "c");
		await engine.SyncOnceAsync(Ct);

		Assert.Equal(2, engine.ErrorCount);
		Assert.Null(AggregateMismatch(engine));
		Assert.Empty(mismatches);
	}

	private static string? AggregateMismatch(SyncEngine engine)
	{
		var synced = engine.Files.Where(f => f.State == FileSyncState.Synced).ToList();
		var expected = (synced.Count, synced.Sum(f => f.Size), engine.Files.Count(f => f.State != FileSyncState.TooLarge), engine.Errors.Count);
		var actual = (engine.SyncedCount, engine.SyncedBytes, engine.SyncableCount, engine.ErrorCount);
		return expected == actual ? null : $"expected {expected}, got {actual}";
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		for (var i = 0; i < 1200 && !condition(); i++)
		{
			await Task.Delay(25, Ct);
		}

		Assert.True(condition());
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private SyncManifestPayload LastManifest() =>
		_server.Transport.Sent.Last(e => e.Type == MessageTypes.SyncManifest).Payload.Deserialize<SyncManifestPayload>(JsonSerializerOptions.Web)!;

	private List<string> SentTypes() => [.. _server.Transport.Sent.Select(e => e.Type)];

	private void AssertNoDeltaDeletes() =>
		Assert.All(
			_server.Transport.Sent.Where(e => e.Type == MessageTypes.SyncDelta),
			e => Assert.Empty(e.Payload.Deserialize<SyncDeltaPayload>(JsonSerializerOptions.Web)!.Deletes));

	private void AssertNoDeltaUpsertsAndDeletesTheSamePath()
	{
		foreach (var envelope in _server.Transport.Sent.Where(e => e.Type == MessageTypes.SyncDelta))
		{
			var delta = envelope.Payload.Deserialize<SyncDeltaPayload>(JsonSerializerOptions.Web)!;
			var deletes = delta.Deletes.ToHashSet(StringComparer.OrdinalIgnoreCase);
			Assert.DoesNotContain(delta.Upserts, u => deletes.Contains(u.Path));
		}
	}

	/// <summary>The server answers matching requests with <c>error {code, message}</c> instead of handling them; replaces an earlier refusal.</summary>
	private void RefuseWhen(Func<Envelope, bool> refuse, string code, string message)
	{
		_realReply ??= _server.Transport.Reply!;
		var real = _realReply;
		_server.Transport.Reply = e => refuse(e)
			? Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Error, new ErrorPayload(code, message), e.CorrelationId))
			: real(e);
	}

	/// <summary>f00.txt … with the content "file 0" ….</summary>
	private void WriteFiles(int count)
	{
		for (var i = 0; i < count; i++)
		{
			_folder.Write($"f{i:00}.txt", $"file {i}");
		}
	}

	private async Task OpenAsync()
	{
		await _engine.OpenFolderAsync();
		Assert.Equal(FolderStatus.Ready, _engine.Folder);
	}
}
