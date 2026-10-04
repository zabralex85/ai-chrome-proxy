using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>The tree's create, rename and delete actions on the folder, and that the next scan carries them to the mirror (<see cref="LoopbackServer"/>).</summary>
public sealed class SyncEngineTreeActionsTests : IDisposable
{
	private const string Repo = "My_Repo";
	private const string ResolveFirst = SyncEngine.ResolveFirst;

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly SyncEngine _engine;

	public SyncEngineTreeActionsTests()
	{
		_engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task CreateFile_Ok_NextScanUploadsIt()
	{
		_folder.Write("keep.txt", "k");
		_folder.Directories.Add("src");
		await SyncedAsync();

		var result = await _engine.CreateFileAsync("src", "new.cs");

		Assert.Equal(new TreeActionResult(true), result);
		Assert.Empty(_folder.Files["src/new.cs"]);
		Assert.False(File.Exists(_server.PathOf(Repo, "src/new.cs")));
		await _engine.SyncOnceAsync(Ct);
		Assert.True(File.Exists(_server.PathOf(Repo, "src/new.cs")));
		Assert.Contains(_engine.Files, f => f.Path == "src/new.cs");
		Assert.Contains(_engine.Activity, a => a.Text == "Created 'src/new.cs'.");
	}

	[Fact]
	public async Task CreateFile_InRoot_UsesTheNameAsPath()
	{
		await SyncedAsync();

		Assert.True((await _engine.CreateFileAsync(string.Empty, "a.txt")).Ok);

		Assert.True(_folder.Files.ContainsKey("a.txt"));
	}

	[Fact]
	public async Task CreateFile_Excluded_NotesItAndDoesNotSyncIt()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		var result = await _engine.CreateFileAsync(string.Empty, ".env");

		Assert.Equal(new TreeActionResult(true, null, "Created; excluded from sync"), result);
		Assert.Contains(_engine.Activity, a => a.Text == "Created '.env'; excluded from sync.");
		await _engine.SyncOnceAsync(Ct);
		Assert.False(File.Exists(_server.PathOf(Repo, ".env")));
	}

	[Fact]
	public async Task CreateFolder_ShowsInDirectories_NothingOnTheMirror()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		var result = await _engine.CreateFolderAsync(string.Empty, "empty");

		Assert.True(result.Ok);
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(["empty"], _engine.Directories);
		Assert.False(Directory.Exists(_server.PathOf(Repo, "empty")));
	}

	[Fact]
	public async Task Directories_HideExcludedFolders()
	{
		_folder.Write("keep.txt", "k");
		_folder.Directories.Add("build");
		_folder.Directories.Add("docs");
		_folder.Write(".gitignore", "bu*\n");
		await SyncedAsync();

		Assert.Equal(["docs"], _engine.Directories);
	}

	[Fact]
	public async Task RenameFile_OldDeletedNewUploaded_NoConflict()
	{
		_folder.Write("old.txt", "content");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		var result = await _engine.RenameAsync("old.txt", "new.txt");

		Assert.True(result.Ok);
		await _engine.SyncOnceAsync(Ct);
		Assert.False(File.Exists(_server.PathOf(Repo, "old.txt")));
		Assert.Equal("content", File.ReadAllText(_server.PathOf(Repo, "new.txt")));
		Assert.Empty(_engine.Remote);
		Assert.Equal(0, _engine.ConflictCount);
		Assert.Contains(_engine.Activity, a => a.Text == "Renamed 'old.txt' to 'new.txt'.");

		// Nothing comes back from the server: the next scan has nothing to do.
		_server.Transport.Sent.Clear();
		await _engine.SyncOnceAsync(Ct);
		Assert.Empty(_server.Transport.Sent);
	}

	[Fact]
	public async Task RenameFolder_FilesFollowOnTheMirror()
	{
		_folder.Write("a/x.txt", "x");
		_folder.Write("a/deep/y.txt", "y");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		Assert.True((await _engine.RenameAsync("a", "b")).Ok);

		await _engine.SyncOnceAsync(Ct);
		Assert.False(Directory.Exists(_server.PathOf(Repo, "a")));
		Assert.Equal("x", File.ReadAllText(_server.PathOf(Repo, "b/x.txt")));
		Assert.Equal("y", File.ReadAllText(_server.PathOf(Repo, "b/deep/y.txt")));
		Assert.Empty(_engine.Remote);
	}

	[Fact]
	public async Task RenameFile_CaseOnly_FileStaysOnTheMirror()
	{
		_folder.Write("readme.md", "# hi");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		var result = await _engine.RenameAsync("readme.md", "README.md");

		Assert.True(result.Ok);
		Assert.Equal(["README.md", "keep.txt"], _folder.Files.Keys.Order(StringComparer.Ordinal));
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal("# hi", File.ReadAllText(_server.PathOf(Repo, "README.md")));
		Assert.Empty(_engine.Remote);
		Assert.Equal(["README.md", "keep.txt"], _engine.Files.Select(f => f.Path));
	}

	[Fact]
	public async Task DeleteFile_MirrorCopyDeletedAtTheNextScan()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await SyncedAsync();

		var result = await _engine.DeleteAsync("a.txt", isFolder: false);

		Assert.True(result.Ok);
		Assert.False(_folder.Files.ContainsKey("a.txt"));
		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
		await _engine.SyncOnceAsync(Ct);
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));
		Assert.True(File.Exists(_server.PathOf(Repo, "b.txt")));
		Assert.Contains(_engine.Activity, a => a.Text == "Deleted 'a.txt'.");
	}

	[Fact]
	public async Task DeleteFolder_AllItsSyncedFilesDeletedOnTheMirror()
	{
		_folder.Write("dir/a.txt", "a");
		_folder.Write("dir/sub/b.txt", "b");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();

		Assert.True((await _engine.DeleteAsync("dir", isFolder: true)).Ok);

		await _engine.SyncOnceAsync(Ct);
		Assert.False(Directory.Exists(_server.PathOf(Repo, "dir")));
		Assert.True(File.Exists(_server.PathOf(Repo, "keep.txt")));
		Assert.Equal(["keep.txt"], _engine.Files.Select(f => f.Path));
	}

	[Fact]
	public async Task Action_WhileARemoteChangeWaits_Refused()
	{
		_folder.Write("a.txt", "v1");
		_folder.Write("docs/b.txt", "b1");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "server a");
		await EditMirrorAsync("docs/b.txt", "server b");
		Assert.Equal(2, _engine.Remote.Count);
		_folder.WriteAccess = true;

		var rename = await _engine.RenameAsync("a.txt", "c.txt");
		var delete = await _engine.DeleteAsync("a.txt", isFolder: false);
		var folderDelete = await _engine.DeleteAsync("docs", isFolder: true);
		var folderRename = await _engine.RenameAsync("docs", "doc");
		var create = await _engine.CreateFileAsync("docs", "b.txt");

		Assert.All([rename, delete, folderDelete, folderRename], r => Assert.Equal(new TreeActionResult(false, "Resolve the server change first"), r));
		Assert.False(create.Ok);
		Assert.Equal(["a.txt", "docs/b.txt", "keep.txt"], _folder.Files.Keys.Order(StringComparer.Ordinal));
		Assert.Empty(_folder.Writes);
	}

	[Fact]
	public async Task HasServerChange_TrueForAWaitingFileAndItsFolder_FalseElsewhere()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		await EditMirrorAsync("docs/new.txt", "from the server");

		Assert.True(_engine.HasServerChange("docs/new.txt"));
		Assert.True(_engine.HasServerChange("docs"));
		Assert.False(_engine.HasServerChange("keep.txt"));
		Assert.False(_engine.HasServerChange("doc"));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CanRenameFolders_FollowsTheBrowser(bool supported)
	{
		_folder.FolderMove = supported;

		Assert.Equal(supported, await _engine.CanRenameFoldersAsync());
	}

	[Fact]
	public async Task Create_WhereARemoteChangeWaits_RefusedByTheRemoteCheck()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		await EditMirrorAsync("new.txt", "from the server");
		_folder.WriteAccess = true;
		Assert.DoesNotContain(_engine.Files, f => f.Path == "new.txt");

		var result = await _engine.CreateFileAsync(string.Empty, "new.txt");

		Assert.Equal(new TreeActionResult(false, "Resolve the server change first"), result);
		Assert.False(_folder.Files.ContainsKey("new.txt"));
	}

	[Fact]
	public async Task Rename_TargetWithAWaitingChangeUnderIt_Refused()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		await EditMirrorAsync("docs/new.txt", "from the server");
		_folder.WriteAccess = true;

		var result = await _engine.RenameAsync("a.txt", "docs");

		Assert.Equal(new TreeActionResult(false, "Resolve the server change first"), result);
		Assert.True(_folder.Files.ContainsKey("a.txt"));
	}

	[Fact]
	public async Task Action_PathDiffersInCaseFromAWaitingChange_Refused()
	{
		_folder.Write("a.txt", "v1");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		await EditMirrorAsync("a.txt", "server a");
		_folder.WriteAccess = true;

		Assert.Equal(ResolveFirst, (await _engine.DeleteAsync("A.TXT", isFolder: false)).Error);
		Assert.Equal(ResolveFirst, (await _engine.RenameAsync("A.TXT", "b.txt")).Error);
	}

	[Fact]
	public async Task DeleteLastSyncedFile_SaysTheServerKeepsIt_AndTheCycleDoesNotBlameAccess()
	{
		_folder.Write("only.txt", "x");
		await SyncedAsync();

		var result = await _engine.DeleteAsync("only.txt", isFolder: false);

		Assert.Equal(new TreeActionResult(true, null, SyncEngine.KeepsLastFiles), result);
		Assert.Contains(_engine.Activity, a => a.Text == SyncEngine.KeepsLastFiles);
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(SyncEngine.KeepsLastFiles, _engine.Problem);
		Assert.True(File.Exists(_server.PathOf(Repo, "only.txt")));
	}

	[Fact]
	public async Task Delete_NotTheLastFile_NoKeepNote()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("b.txt", "b");
		await SyncedAsync();

		Assert.Null((await _engine.DeleteAsync("a.txt", isFolder: false)).Note);
	}

	[Fact]
	public async Task Rename_ForgetsABackedOffFailure_SoRenamingBackUploads()
	{
		_folder.Write("keep.txt", "k");
		_folder.Write("b.txt", "bb");
		_folder.ReadFailures.Add("b.txt");
		await SyncedAsync();
		Assert.Equal(FileSyncState.Error, _engine.FileAt("b.txt")!.State);

		Assert.True((await _engine.RenameAsync("b.txt", "c.txt")).Ok);
		_folder.ReadFailures.Clear();
		await _engine.SyncOnceAsync(Ct);
		Assert.True(File.Exists(_server.PathOf(Repo, "c.txt")));
		Assert.True((await _engine.RenameAsync("c.txt", "b.txt")).Ok);
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.Synced, _engine.FileAt("b.txt")!.State);
		Assert.True(File.Exists(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task Delete_ForgetsFailuresUnderTheFolder()
	{
		_folder.Write("keep.txt", "k");
		_folder.Write("d/b.txt", "bb");
		_folder.ReadFailures.Add("d/b.txt");
		await SyncedAsync();
		Assert.Equal(FileSyncState.Error, _engine.FileAt("d/b.txt")!.State);

		Assert.True((await _engine.DeleteAsync("d", isFolder: true)).Ok);
		_folder.ReadFailures.Clear();
		_folder.Write("d/b.txt", "bb");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.Synced, _engine.FileAt("d/b.txt")!.State);
	}

	[Fact]
	public async Task Action_WhileInConflict_Refused()
	{
		_folder.Write("a.txt", "v1");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		_folder.WriteAccess = true;
		_folder.Write("a.txt", "mine");
		await EditMirrorAsync("a.txt", "server a");
		await _engine.SyncOnceAsync(Ct);
		Assert.Equal(1, _engine.ConflictCount);

		var result = await _engine.DeleteAsync("a.txt", isFolder: false);

		Assert.Equal(new TreeActionResult(false, "Resolve the server change first"), result);
		Assert.True(_folder.Files.ContainsKey("a.txt"));
	}

	[Fact]
	public async Task NoWriteAccess_AsksFromTheClick_ThenActs()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		Assert.False(_folder.WriteAccess);

		var result = await _engine.CreateFileAsync(string.Empty, "a.txt");

		Assert.True(result.Ok);
		Assert.True(_engine.CanWrite);
		Assert.True(_folder.Files.ContainsKey("a.txt"));
	}

	[Fact]
	public async Task NoWriteAccess_Refused_ErrorAndNothingChanged()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		_folder.ClickFailure = new JSException("NotAllowedError: policy");

		var results = new[]
		{
			await _engine.CreateFileAsync(string.Empty, "n.txt"),
			await _engine.CreateFolderAsync(string.Empty, "d"),
			await _engine.RenameAsync("a.txt", "b.txt"),
			await _engine.DeleteAsync("a.txt", isFolder: false),
		};

		Assert.All(results, r => Assert.Equal(new TreeActionResult(false, "Write access to the folder was not granted; nothing was changed."), r));
		Assert.Equal(["a.txt", "keep.txt"], _folder.Files.Keys.Order(StringComparer.Ordinal));
		Assert.Empty(_folder.Directories);
		Assert.Equal(4, _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error && a.Text.StartsWith("Could not get write access", StringComparison.Ordinal)));
		Assert.Equal(4, _engine.Activity.Count(a => a.Kind == SyncActivityKind.Error));
	}

	[Fact]
	public async Task Names_Validated_AndTheRootIsRefused()
	{
		_folder.Write("a.txt", "a");
		_folder.Write("dir/x.txt", "x");
		await SyncedAsync();
		_folder.WriteAccess = true;

		Assert.Equal("Enter a name.", (await _engine.CreateFileAsync(string.Empty, " ")).Error);
		Assert.Equal("A name cannot contain / or \\.", (await _engine.CreateFolderAsync(string.Empty, "a/b")).Error);
		Assert.Equal("'A.TXT' already exists here.", (await _engine.CreateFileAsync(string.Empty, "A.TXT")).Error);
		Assert.Equal("'dir' already exists here.", (await _engine.RenameAsync("a.txt", "dir")).Error);
		Assert.Equal(SyncPath.GetError("CON"), (await _engine.CreateFileAsync(string.Empty, "CON")).Error);
		Assert.False((await _engine.RenameAsync(string.Empty, "x")).Ok);
		Assert.False((await _engine.DeleteAsync(string.Empty, isFolder: true)).Ok);
		Assert.Empty(_folder.Writes);
	}

	[Fact]
	public async Task BrowserFailure_BecomesAnErrorResult_NeverThrows()
	{
		_folder.Write("a.txt", "a");
		await SyncedAsync();
		_folder.WriteAccess = true;
		_folder.OperationFailure = new JSException("NotFoundError: gone");

		var result = await _engine.DeleteAsync("a.txt", isFolder: false);

		Assert.Equal(new TreeActionResult(false, "NotFoundError: gone"), result);
		Assert.Contains(_engine.Activity, a => a.Kind == SyncActivityKind.Error && a.Text == "Could not delete 'a.txt': NotFoundError: gone");
	}

	[Fact]
	public async Task CountFiles_CountsInsideTheFolder_NullWhenItFails()
	{
		_folder.Write("dir/a.txt", "a");
		_folder.Write("dir/b/c.txt", "c");
		await SyncedAsync();

		Assert.Equal(new FileCount(2, false), await _engine.CountFilesAsync("dir"));
		Assert.Null(await _engine.CountFilesAsync("nope"));
	}

	[Fact]
	public async Task Action_WakesTheLoop_ForAnImmediateScan()
	{
		_folder.Write("keep.txt", "k");
		await SyncedAsync();
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var run = Task.Run(() => _engine.RunAsync(cts.Token), Ct);
			await WaitUntilAsync(() => _engine.NextScan is not null);

			await _engine.CreateFileAsync(string.Empty, "woke.txt");
			await WaitUntilAsync(() => File.Exists(_server.PathOf(Repo, "woke.txt")));

			await cts.CancelAsync();
			await run;
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var until = DateTime.UtcNow.AddSeconds(5);
		while (!condition() && DateTime.UtcNow < until)
		{
			await Task.Delay(10, Ct);
		}

		Assert.True(condition());
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
