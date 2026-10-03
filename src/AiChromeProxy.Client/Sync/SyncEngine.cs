using System.Globalization;
using System.Text.Json;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

public enum FolderStatus
{
	/// <summary>No folder picked yet.</summary>
	None,

	/// <summary>A folder is remembered but the browser needs a click to grant access again.</summary>
	NeedsPermission,

	Ready,
}

public enum SyncPhase
{
	Idle,
	Scanning,
	Uploading,
	Synced,
	Failed,
}

/// <summary>
/// One-way sync of the picked folder to the server's mirror: scan → (first time or after a reconnect) open + full manifest,
/// otherwise a delta → upload what the server needs, one file at a time in chunks. Rescans every <see cref="ScanInterval"/>
/// while the tab is visible and immediately on focus (ponytail: polling; switch to FileSystemObserver once it is stable).
/// </summary>
public sealed class SyncEngine(ITransport transport, IFolderAccess folder, TimeProvider time)
{
	/// <summary>Files and folders the walk may return before the folder is refused (excluded ones included; protects the tab's memory).</summary>
	public const int MaxScanEntries = 100_000;

	/// <summary>Entries kept in <see cref="Activity"/>.</summary>
	public const int MaxActivity = 500;

	public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	/// <summary>During uploads <see cref="Changed"/> fires at most this often (a 20k-file first sync must not re-render 20k times).</summary>
	public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

	private readonly List<SyncActivity> _activity = [];
	private Dictionary<string, ManifestEntry> _known = new(StringComparer.Ordinal);
	private SyncFile[] _files = [];
	private Dictionary<string, int> _fileIndex = new(StringComparer.Ordinal);
	private int _cycleRunning;
	private long _lastRaise;
	private string? _repo;
	private bool _visible = true;
	private bool _reconnecting;
	private CancellationTokenSource _wake = new();

	/// <summary>Raised after every visible state change; the UI re-renders.</summary>
	public event Action? Changed;

	public FolderStatus Folder { get; private set; }

	public string? FolderName { get; private set; }

	public SyncPhase Phase { get; private set; }

	/// <summary>
	/// Everything in the folder except excluded files, sorted by path. A new list only when a scan finds a different
	/// set of files or states (the explorer rebuilds its tree then); upload progress updates entries in place.
	/// </summary>
	public IReadOnlyList<SyncFile> Files => _files;

	public int UploadDone { get; private set; }

	public int UploadTotal { get; private set; }

	public DateTimeOffset? LastSync { get; private set; }

	public DateTimeOffset? NextScan { get; private set; }

	/// <summary>Why the last cycle failed as a whole (connection, too many files, lost access); null when it did not.</summary>
	public string? Problem { get; private set; }

	/// <summary>
	/// The server refused the full manifest of a fresh session (too many files, an empty folder over a full mirror): retrying cannot help,
	/// so no cycle runs until <see cref="OpenFolderAsync"/> (Change folder) or <see cref="RestoreAccessAsync"/> (Restore access).
	/// </summary>
	public bool Blocked { get; private set; }

	/// <summary>The folder-level problem followed by every file in error, as "path: reason".</summary>
	public IReadOnlyList<string> Errors =>
		[.. Problem is null ? [] : new[] { Problem }, .. Files.Where(f => f.State == FileSyncState.Error).Select(f => $"{f.Path}: {f.Error}")];

	/// <summary>The "Actions history": newest first, at most <see cref="MaxActivity"/> entries; <see cref="Changed"/> fires when it grows.</summary>
	public IReadOnlyList<SyncActivity> Activity
	{
		get
		{
			lock (_activity)
			{
				return [.. _activity];
			}
		}
	}

	/// <summary>Restores the remembered folder and starts listening to the connection and the tab's visibility.</summary>
	public async Task InitializeAsync()
	{
		transport.StateChanged -= OnTransportStateChanged;
		transport.StateChanged += OnTransportStateChanged;
		await folder.WatchVisibilityAsync(SetVisible);
		if (await folder.RestoreAsync() is { } grant)
		{
			FolderName = grant.Name;
			Folder = grant.Granted ? FolderStatus.Ready : FolderStatus.NeedsPermission;
		}

		Raise();
	}

	/// <summary>Shows the picker (call it straight from the click); a new folder starts a fresh session (full manifest).</summary>
	public async Task OpenFolderAsync()
	{
		if (await folder.PickAsync() is not { } name)
		{
			return;
		}

		FolderName = name;
		Folder = FolderStatus.Ready;
		Blocked = false;
		Problem = null;
		SetFiles([]);
		_repo = null;
		Log(SyncActivityKind.FolderOpened, $"Opened folder '{name}'.");
		Wake();
		Raise();
	}

	/// <summary>Asks the browser for access to the remembered folder again (call it straight from the <b>Restore access</b> click).</summary>
	public async Task RestoreAccessAsync()
	{
		if (await folder.RequestAccessAsync())
		{
			Folder = FolderStatus.Ready;
			Blocked = false;
			Problem = null;
			Wake();
		}

		Raise();
	}

	public void SetVisible(bool visible)
	{
		_visible = visible;
		if (visible)
		{
			Wake();
		}
	}

	/// <summary>The explorer entry for a path, or null when the path is not (or no longer) listed.</summary>
	public SyncFile? FileAt(string path) => _fileIndex.TryGetValue(path, out var i) ? _files[i] : null;

	/// <summary>Runs <see cref="SyncOnceAsync"/> now and then every <see cref="ScanInterval"/> (or when woken) until cancelled; returns when cancelled.</summary>
	public async Task RunAsync(CancellationToken ct)
	{
		// A wake-up from before the loop runs (e.g. the folder was just picked) is covered by its first cycle.
		_wake = new CancellationTokenSource();
		try
		{
			while (!ct.IsCancellationRequested)
			{
				if (Folder == FolderStatus.Ready && _visible && transport.State == TransportState.Connected)
				{
					await SyncOnceAsync(ct);
				}

				NextScan = time.GetUtcNow() + ScanInterval;
				Raise();
				await WaitForNextScanAsync(ct);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Stopped.
		}
	}

	/// <summary>
	/// One cycle: scan, tell the server (full manifest or delta), upload what it needs. Returns at once while another cycle runs
	/// or while <see cref="Blocked"/>. Never throws except on cancellation.
	/// </summary>
	public async Task SyncOnceAsync(CancellationToken ct)
	{
		if (Blocked || Interlocked.Exchange(ref _cycleRunning, 1) == 1)
		{
			return;
		}

		var previousProblem = Problem;
		try
		{
			Phase = SyncPhase.Scanning;
			Raise();
			if (await ScanAsync() is not { } entries)
			{
				Phase = SyncPhase.Failed;
				return;
			}

			// A reconnect may reset _repo while this cycle runs: the cycle keeps using the repo it started with.
			var repo = _repo;
			var started = time.GetTimestamp();
			List<string> need;
			List<string> deleted = [];
			bool pass;
			if (repo is null)
			{
				repo = await OpenAsync(ct);
				pass = true;
				Log(SyncActivityKind.PassStarted, $"Full sync of {FileCount(entries.Count)}.");
				need = await SendManifestAsync(repo, entries, ct);
			}
			else
			{
				var pages = ManifestPlanner.DeltaPages(repo, _known, entries);
				deleted = [.. pages.SelectMany(p => p.Deletes)];
				pass = pages.Count > 0;
				if (pass)
				{
					Log(SyncActivityKind.PassStarted, $"Sync of {pages.Sum(p => p.Upserts.Count)} changed and {deleted.Count} deleted files.");
				}

				need = await SendDeltaAsync(pages, ct);
			}

			// The server now has every entry except the ones it asked for.
			var needed = need.ToHashSet(StringComparer.Ordinal);
			_known = entries.Where(e => !needed.Contains(e.Path)).ToDictionary(e => e.Path, StringComparer.Ordinal);
			foreach (var f in _files.Where(f => f.State == FileSyncState.Pending && !needed.Contains(f.Path)).ToList())
			{
				SetFileState(f.Path, FileSyncState.Synced, null);
			}

			var (uploaded, failed) = await UploadAsync(repo, entries.Where(e => needed.Contains(e.Path)).ToList(), ct);
			if (pass)
			{
				LogPass(uploaded, deleted, failed, time.GetElapsedTime(started));
			}

			Problem = null;
			Phase = SyncPhase.Synced;
			LastSync = time.GetUtcNow();
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			// The server may have lost the session (reconnect, restart): start over with a full manifest.
			_repo = null;
			Problem = Blocked ? $"{ex.Message} Sync is paused: change the folder or restore access." : ex.Message;
			Phase = SyncPhase.Failed;
		}
		finally
		{
			// A problem is logged when it appears, not on every retry.
			if (Problem is not null && Problem != previousProblem)
			{
				Log(Folder == FolderStatus.NeedsPermission ? SyncActivityKind.AccessLost : SyncActivityKind.Error, Problem);
			}

			Volatile.Write(ref _cycleRunning, 0);
			Raise();
		}
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private static string FileCount(int count) => count == 1 ? "1 file" : $"{count} files";

	/// <summary>"Uploaded 5 files: a, b, c and 2 more."</summary>
	private static string Group(string verb, IReadOnlyList<string> items)
	{
		const int Shown = 3;
		return $"{verb} {FileCount(items.Count)}: {string.Join(", ", items.Take(Shown))}" + (items.Count > Shown ? $" and {items.Count - Shown} more." : ".");
	}

	private void LogPass(List<ManifestEntry> uploaded, List<string> deleted, List<string> failed, TimeSpan duration)
	{
		if (uploaded.Count > 0)
		{
			Log(SyncActivityKind.Uploaded, Group("Uploaded", [.. uploaded.Select(e => e.Path)]));
		}

		if (deleted.Count > 0)
		{
			Log(SyncActivityKind.Deleted, Group("Deleted", deleted));
		}

		if (failed.Count > 0)
		{
			Log(SyncActivityKind.Error, $"Could not upload {FileCount(failed.Count)}: {failed[0]}" + (failed.Count > 1 ? $" and {failed.Count - 1} more." : string.Empty));
		}

		Log(
			SyncActivityKind.PassFinished,
			string.Create(CultureInfo.InvariantCulture, $"Uploaded {FileCount(uploaded.Count)}, {uploaded.Sum(e => e.Size):N0} bytes in {duration.TotalSeconds:0.0} s."));
	}

	private void Log(SyncActivityKind kind, string text)
	{
		lock (_activity)
		{
			_activity.Insert(0, new SyncActivity(time.GetUtcNow(), kind, text));
			if (_activity.Count > MaxActivity)
			{
				_activity.RemoveAt(MaxActivity);
			}
		}
	}

	/// <returns>The entries to sync (sorted by path), or null when the folder cannot be synced (<see cref="Problem"/> says why).</returns>
	private async Task<List<ManifestEntry>?> ScanAsync()
	{
		FolderScan scan;
		try
		{
			scan = await folder.ScanAsync(IgnoreRules.BuiltInDirectories, MaxScanEntries);
		}
		catch (JSException)
		{
			if (await folder.RestoreAsync() is not { Granted: true })
			{
				Folder = FolderStatus.NeedsPermission;
				Problem = "Access to the folder was lost; click Restore access.";
				return null;
			}

			throw;
		}

		if (scan.Truncated)
		{
			Problem = string.Create(CultureInfo.InvariantCulture, $"The folder has more than {MaxScanEntries:N0} files and folders; pick a smaller folder.");
			return null;
		}

		var rules = IgnoreRules.Create(await folder.ReadTextAsync(".gitignore"));
		var included = scan.Files.Where(f => !rules.IsIgnored(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

		// Too large and invalid paths are decided first: they are never hashed, sent or retried (only listed with the reason).
		var syncable = included.Where(f => f.Size <= SyncLimits.MaxFileSize && SyncPath.IsValid(f.Path)).ToList();
		if (syncable.Count > SyncLimits.MaxFiles)
		{
			Problem = string.Create(CultureInfo.InvariantCulture, $"The folder has more than {SyncLimits.MaxFiles:N0} files to sync; pick a smaller folder or extend its .gitignore.");
			return null;
		}

		var hashes = await folder.HashAsync(syncable.Select(f => f.Path).ToList());
		var hashByPath = syncable.Select((f, i) => (f.Path, Hash: hashes[i])).ToDictionary(x => x.Path, x => x.Hash, StringComparer.Ordinal);
		var entries = new List<ManifestEntry>();
		var files = new List<SyncFile>();
		foreach (var f in included)
		{
			if (f.Size > SyncLimits.MaxFileSize)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.TooLarge));
			}
			else if (SyncPath.GetError(f.Path) is { } error)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.Error, error));
			}
			else if (hashByPath[f.Path] is not { } hash)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.Error, "Could not read the file."));
			}
			else
			{
				var entry = new ManifestEntry(f.Path, f.Size, hash);
				entries.Add(entry);
				var synced = _known.TryGetValue(f.Path, out var known) && known == entry;
				files.Add(new SyncFile(f.Path, f.Size, hash, synced ? FileSyncState.Synced : FileSyncState.Pending));
			}
		}

		if (!files.SequenceEqual(_files))
		{
			SetFiles(files);
		}

		Raise();
		return entries;
	}

	/// <returns>The repo name the server uses (sanitized folder name).</returns>
	private async Task<string> OpenAsync(CancellationToken ct)
	{
		var opened = await RequestAsync(MessageTypes.SyncOpen, new SyncOpenPayload(FolderName!), ct);
		_repo = Read<SyncOpenPayload>(opened).Repo;
		_known.Clear();
		return _repo;
	}

	private async Task<List<string>> SendManifestAsync(string repo, List<ManifestEntry> entries, CancellationToken ct)
	{
		var need = new List<string>();
		foreach (var page in ManifestPlanner.ManifestPages(repo, entries))
		{
			try
			{
				need.AddRange(Read<SyncNeedPayload>(await RequestAsync(MessageTypes.SyncManifest, page, ct)).Paths);
			}
			catch (RequestFailedException) when (_repo == repo)
			{
				// The session was just opened (and not lost to a reconnect since), so the refusal is about the folder itself.
				Blocked = true;
				throw;
			}
		}

		return need;
	}

	/// <summary>Never lists a path in both upserts and deletes (<see cref="ManifestPlanner.DeltaPages"/> puts them on separate pages).</summary>
	private async Task<List<string>> SendDeltaAsync(List<SyncDeltaPayload> pages, CancellationToken ct)
	{
		var need = new List<string>();
		foreach (var page in pages)
		{
			need.AddRange(Read<SyncNeedPayload>(await RequestAsync(MessageTypes.SyncDelta, page, ct)).Paths);
		}

		return need;
	}

	/// <summary>
	/// Sequential uploads; a file that fails (refused, changed or unreadable) is marked and sent again on the next scan only,
	/// a lost connection ends the cycle.
	/// </summary>
	/// <returns>The stored entries and the failed files as "path: reason".</returns>
	private async Task<(List<ManifestEntry> Uploaded, List<string> Failed)> UploadAsync(string repo, List<ManifestEntry> entries, CancellationToken ct)
	{
		var uploaded = new List<ManifestEntry>();
		var failed = new List<string>();
		UploadDone = 0;
		UploadTotal = entries.Count;
		Phase = SyncPhase.Uploading;
		Raise();
		foreach (var entry in entries)
		{
			try
			{
				await UploadFileAsync(repo, entry, ct);
				_known[entry.Path] = entry;
				SetFileState(entry.Path, FileSyncState.Synced, null);
				uploaded.Add(entry);
			}
			catch (Exception ex) when (ex is RequestFailedException or IOException or JSException)
			{
				SetFileState(entry.Path, FileSyncState.Error, ex.Message);
				failed.Add($"{entry.Path}: {ex.Message}");
			}

			UploadDone++;
			if (time.GetElapsedTime(_lastRaise) >= ProgressInterval)
			{
				Raise();
			}
		}

		return (uploaded, failed);
	}

	private async Task UploadFileAsync(string repo, ManifestEntry entry, CancellationToken ct)
	{
		for (long offset = 0; ; offset += SyncLimits.ChunkSize)
		{
			var length = (int)Math.Min(SyncLimits.ChunkSize, entry.Size - offset);
			var data = length == 0 ? [] : await folder.ReadChunkAsync(entry.Path, offset, length);
			if (data.Length != length)
			{
				throw new IOException("The file changed while it was uploaded; it is sent again on the next scan.");
			}

			var last = offset + length == entry.Size;
			var chunk = new SyncChunkPayload(repo, entry.Path, offset, SyncData.Encode(data), last, last ? entry.Sha256 : null);
			if (last)
			{
				await RequestAsync(MessageTypes.SyncChunk, chunk, ct);
				return;
			}

			// Not awaited for a reply: the server answers only the last chunk (or an error, which the last chunk then gets too).
			await transport.SendAsync(Envelope.Create(MessageTypes.SyncChunk, chunk), ct);
		}
	}

	private Task<Envelope> RequestAsync<T>(string type, T payload, CancellationToken ct) =>
		transport.RequestAsync(Envelope.Create(type, payload), RequestTimeout, ct);

	private void SetFiles(IReadOnlyList<SyncFile> files)
	{
		_files = [.. files];
		_fileIndex = files.Select((f, i) => (f.Path, i)).ToDictionary(x => x.Path, x => x.i, StringComparer.Ordinal);
	}

	/// <summary>No-op for a path no longer listed (a folder change replaced the list while the cycle ran).</summary>
	private void SetFileState(string path, FileSyncState state, string? error)
	{
		if (_fileIndex.TryGetValue(path, out var i))
		{
			_files[i] = _files[i] with { State = state, Error = error };
		}
	}

	/// <summary>Sessions live per connection: after a reconnect the next cycle opens again and sends the full manifest.</summary>
	private void OnTransportStateChanged(TransportState state)
	{
		if (state != TransportState.Connected)
		{
			_repo = null;
			_reconnecting |= state == TransportState.Reconnecting;
			return;
		}

		if (_reconnecting)
		{
			_reconnecting = false;
			Log(SyncActivityKind.Reconnected, "Reconnected; the next scan sends the full manifest.");
			Raise();
		}

		Wake();
	}

	private async Task WaitForNextScanAsync(CancellationToken ct)
	{
		using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token))
		{
			try
			{
				await Task.Delay(ScanInterval, time, wait.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				// Woken up early.
			}
		}

		if (_wake.IsCancellationRequested)
		{
			_wake.Dispose();
			_wake = new CancellationTokenSource();
		}
	}

	private void Wake() => _wake.Cancel();

	private void Raise()
	{
		_lastRaise = time.GetTimestamp();
		Changed?.Invoke();
	}
}
