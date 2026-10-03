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
/// One-way sync of the picked folder to the server's mirror: scan → (first time, after a reconnect, and every
/// <see cref="FullManifestInterval"/>) the full manifest with its keep list, otherwise a delta → upload what the server needs, one file at a time in chunks. Rescans every <see cref="ScanInterval"/>
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

	/// <summary>How often a session sends the full manifest again (with its keep list), so the mirror never stays stale; cheap, the server caches hashes.</summary>
	public static readonly TimeSpan FullManifestInterval = TimeSpan.FromMinutes(10);

	/// <summary>A file whose upload failed is not tried again with the same size and hash before this much time has passed.</summary>
	public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

	/// <summary>During uploads <see cref="Changed"/> fires at most this often (a 20k-file first sync must not re-render 20k times).</summary>
	public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

	private const string AccessLost = "Access to the folder was lost; click Restore access.";
	private const string GitIgnore = ".gitignore";
	private const string GitIgnoreUnreadable = "The folder's .gitignore could not be read; nothing was synced, so that ignored files are not uploaded.";
	private const string LooksEmpty = "The folder looks empty; nothing was deleted. Check access or pick the folder again.";
	private const string PassAborted = "The connection was lost during the sync; the next scan starts over.";
	private const string KeptUnread = "Could not be read; the mirror keeps the last synced version.";
	private const string Unreadable = "Could not read the file.";
	private const string Unlisted = "Could not list this folder; its files are neither uploaded nor deleted.";

	private readonly Lock _activityGate = new();

	/// <summary>Upload failures by path: the same size and hash is not tried again before <c>Until</c>, and its failure is logged once.</summary>
	private readonly Dictionary<string, (ManifestEntry Entry, string Error, DateTimeOffset Until)> _failures = new(StringComparer.Ordinal);

	/// <summary>
	/// What the server has, as far as this page knows (kept across reconnects): a path that disappears from the folder is deleted there.
	/// An empty hash means its content there is unknown (upload pending or failed, or kept while it cannot be read).
	/// </summary>
	private Dictionary<string, ManifestEntry> _known = new(StringComparer.Ordinal);
	private SyncFile[] _files = [];
	private Dictionary<string, int> _fileIndex = new(StringComparer.Ordinal);
	private IReadOnlyList<string> _unlistedDirectories = [];
	private IReadOnlyList<string>? _errors;
	private string? _problem;
	private IReadOnlyList<SyncActivity> _activity = [];
	private long _activityCount;

	// Kept up to date by SetFiles and SetFileState, so the UI reads them without scanning Files on every render.
	private int _syncedCount;
	private long _syncedBytes;
	private int _syncableCount;
	private int _fileErrorCount;

	/// <summary>Bumped by every folder change: a cycle started for an earlier folder stops at its next check and writes nothing.</summary>
	private int _generation;
	private int _cycleRunning;
	private long _lastRaise;
	private long _fullManifestAt;
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
	public string? Problem
	{
		get => _problem;
		private set
		{
			if (_problem != value)
			{
				_problem = value;
				_errors = null;
			}
		}
	}

	/// <summary>
	/// The server refused the full manifest of a fresh session (too many files, an empty folder over a full mirror): retrying cannot help,
	/// so no cycle runs until <see cref="OpenFolderAsync"/> (Change folder) or <see cref="RestoreAccessAsync"/> (Restore access).
	/// </summary>
	public bool Blocked { get; private set; }

	/// <summary>
	/// The folder-level problem, then every folder the walk could not list (as "dir/: reason") and every file in error ("path: reason").
	/// The same list until one of them changes.
	/// </summary>
	public IReadOnlyList<string> Errors => _errors ??=
	[
		.. Problem is null ? [] : new[] { Problem },
		.. _unlistedDirectories.Select(d => $"{d}: {Unlisted}"),
		.. Files.Where(f => f.State == FileSyncState.Error).Select(f => $"{f.Path}: {f.Error}"),
	];

	/// <summary>Files in <see cref="FileSyncState.Synced"/>.</summary>
	public int SyncedCount => _syncedCount;

	/// <summary>Total size of the synced files.</summary>
	public long SyncedBytes => _syncedBytes;

	/// <summary>Files that are not too large to sync.</summary>
	public int SyncableCount => _syncableCount;

	/// <summary><c>Errors.Count</c> without building the list.</summary>
	public int ErrorCount => (Problem is null ? 0 : 1) + _unlistedDirectories.Count + _fileErrorCount;

	/// <summary>
	/// The "Actions history": newest first, at most <see cref="MaxActivity"/> entries. An immutable snapshot, replaced when
	/// something is logged (<see cref="Changed"/> fires then).
	/// </summary>
	public IReadOnlyList<SyncActivity> Activity => Volatile.Read(ref _activity);

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

		_generation++;
		FolderName = name;
		Folder = FolderStatus.Ready;
		Blocked = false;
		Problem = null;
		SetFiles([]);
		_unlistedDirectories = [];
		_known = new(StringComparer.Ordinal);
		_failures.Clear();
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
		var generation = _generation;
		try
		{
			Phase = SyncPhase.Scanning;
			Raise();
			if (await ScanAsync(generation) is not { } scan)
			{
				Phase = SyncPhase.Failed;
				return;
			}

			// A reconnect may reset _repo while this cycle runs: the cycle checks it still runs in the session it started with.
			var entries = scan.Entries;
			var repo = _repo;
			var started = time.GetTimestamp();
			var backedOff = entries.Where(e => BackedOff(e) is not null).ToDictionary(e => e.Path, StringComparer.Ordinal);
			var listed = entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
			bool Kept(string path) => !listed.Contains(path) && SyncPath.IsKept(path, scan.Keep);
			List<string> need;
			List<string> deleted = [];
			bool pass;

			// A fresh session (first pass, reconnect, folder change) and then every FullManifestInterval: the full manifest, so nothing
			// stale stays on the mirror; the keep list protects what this scan could not sync.
			var full = repo is null || time.GetElapsedTime(_fullManifestAt) >= FullManifestInterval;
			repo ??= await OpenAsync(generation, ct);
			if (full)
			{
				pass = true;
				Log(SyncActivityKind.PassStarted, $"Full sync of {FileCount(entries.Count)}.");
				need = await SendManifestAsync(repo, generation, entries, scan.Keep, ct);
				_fullManifestAt = time.GetTimestamp();
			}
			else
			{
				// What is kept is neither upserted nor deleted; a backed-off file is left out as if the server had it.
				var known = _known.Where(k => !Kept(k.Key)).ToDictionary(StringComparer.Ordinal);
				foreach (var (path, entry) in backedOff)
				{
					known[path] = entry;
				}

				var pages = ManifestPlanner.DeltaPages(repo, known, entries);
				deleted = [.. pages.SelectMany(p => p.Deletes)];
				pass = pages.Count > 0;
				if (pass)
				{
					Log(SyncActivityKind.PassStarted, $"Sync of {pages.Sum(p => p.Upserts.Count)} changed and {deleted.Count} deleted files.");
				}

				need = await SendDeltaAsync(repo, generation, pages, ct);
			}

			// The server now has every entry except the ones it asked for and the backed-off ones: those (until uploaded) and the kept
			// files stay known with an unknown hash, so that their deletion is sent too.
			EnsureCurrent(generation, repo);
			var needed = need.ToHashSet(StringComparer.Ordinal);
			var nowKnown = entries.ToDictionary(e => e.Path, e => needed.Contains(e.Path) || backedOff.ContainsKey(e.Path) ? e with { Sha256 = string.Empty } : e, StringComparer.Ordinal);
			foreach (var (path, entry) in _known.Where(k => Kept(k.Key)))
			{
				nowKnown.TryAdd(path, entry);
			}

			foreach (var path in scan.Keep.Where(k => !k.EndsWith('/')))
			{
				nowKnown.TryAdd(path, new ManifestEntry(path, 0, string.Empty));
			}

			_known = nowKnown;
			foreach (var f in _files.Where(f => f.State == FileSyncState.Pending && !needed.Contains(f.Path)).ToList())
			{
				SetFileState(f.Path, FileSyncState.Synced, null);
			}

			// Backed-off files are not uploaded before their backoff ends.
			var upload = entries.Where(e => needed.Contains(e.Path) && !backedOff.ContainsKey(e.Path)).ToList();
			var (uploaded, failed) = await UploadAsync(repo, generation, upload, ct);
			if (pass)
			{
				LogPass(uploaded, deleted, failed, time.GetElapsedTime(started), full && need.Count == 0 ? entries.Count : null);
			}

			Problem = null;
			Phase = SyncPhase.Synced;
			LastSync = time.GetUtcNow();
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			if (generation != _generation)
			{
				// The folder was changed meanwhile: this cycle's results are void and the next one starts over for the new folder.
				Phase = SyncPhase.Idle;
			}
			else
			{
				// The server may have lost the session (reconnect, restart): start over with a full manifest.
				_repo = null;
				Problem = Blocked ? $"{ex.Message} Sync is paused: change the folder or restore access." : ex.Message;
				Phase = SyncPhase.Failed;
			}
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

	/// <param name="inSync">The file count of a full sync that needed no upload; null otherwise.</param>
	private void LogPass(List<ManifestEntry> uploaded, List<string> deleted, List<string> failed, TimeSpan duration, int? inSync)
	{
		if (inSync is { } count)
		{
			Log(SyncActivityKind.PassFinished, $"Already in sync ({FileCount(count)}).");
			return;
		}

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
		lock (_activityGate)
		{
			var entry = new SyncActivity(time.GetUtcNow(), kind, text, ++_activityCount);
			Volatile.Write(ref _activity, [entry, .. _activity.Take(MaxActivity - 1)]);
		}
	}

	/// <summary>
	/// Scans the folder. Nothing the scan could not see or read becomes a delete: a file that is unreadable or too large, and a folder
	/// that could not be listed, go to the keep list (<see cref="ScanResult.Keep"/>).
	/// </summary>
	/// <returns>The entries to sync, or null when the folder cannot be synced (<see cref="Problem"/> says why).</returns>
	private async Task<ScanResult?> ScanAsync(int generation)
	{
		// The root .gitignore first: the walk does not enter the folders it excludes. A failure here is reported after the walk,
		// which tells a lost access (the walk throws) from an unreadable file.
		string? gitignore = null;
		var gitignoreUnreadable = false;
		try
		{
			gitignore = await folder.ReadTextAsync(GitIgnore);
		}
		catch (JSException)
		{
			gitignoreUnreadable = true;
		}

		EnsureFolder(generation);
		var rules = IgnoreRules.Create(gitignore);
		FolderScan scan;
		try
		{
			scan = await folder.ScanAsync(rules.SkipDirectories, MaxScanEntries);
		}
		catch (JSException)
		{
			if (await folder.RestoreAsync() is not { Granted: true })
			{
				Folder = FolderStatus.NeedsPermission;
				Problem = AccessLost;
				return null;
			}

			throw;
		}

		EnsureFolder(generation);

		// Without its .gitignore the folder's ignored files would be uploaded.
		var skipped = scan.Skipped ?? [];
		if (gitignoreUnreadable || skipped.Contains(GitIgnore))
		{
			Problem = GitIgnoreUnreadable;
			return null;
		}

		if (scan.Truncated)
		{
			Problem = string.Create(CultureInfo.InvariantCulture, $"The folder has more than {MaxScanEntries:N0} files and folders; pick a smaller folder.");
			return null;
		}

		var included = scan.Files.Where(f => !rules.IsIgnored(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

		// Too large and invalid paths are decided first: they are never hashed, sent or retried (only listed with the reason).
		var syncable = included.Where(f => f.Size <= SyncLimits.MaxFileSize && SyncPath.IsValid(f.Path)).ToList();
		if (syncable.Count > SyncLimits.MaxFiles)
		{
			Problem = string.Create(CultureInfo.InvariantCulture, $"The folder has more than {SyncLimits.MaxFiles:N0} files to sync; pick a smaller folder or extend its .gitignore.");
			return null;
		}

		var hashes = await folder.HashAsync(syncable.Select(f => f.Path).ToList());
		EnsureFolder(generation);
		var hashByPath = syncable.Select((f, i) => (f.Path, Hash: hashes[i])).ToDictionary(x => x.Path, x => x.Hash, StringComparer.Ordinal);
		var entries = new List<ManifestEntry>();
		var files = new List<SyncFile>();
		var keep = new HashSet<string>(StringComparer.Ordinal);

		// The mirror keeps the last synced version of what cannot be synced now (a path the server would refuse cannot be on it).
		string? Keep(string path) => keep.Add(path) && _known.TryGetValue(path, out var known) && known.Sha256.Length > 0 ? known.Sha256 : null;

		foreach (var f in included)
		{
			if (f.Size > SyncLimits.MaxFileSize)
			{
				if (SyncPath.IsValid(f.Path))
				{
					Keep(f.Path);
				}

				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.TooLarge));
			}
			else if (SyncPath.GetError(f.Path) is { } error)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.Error, error));
			}
			else if (hashByPath[f.Path] is not { } hash)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.Error, Keep(f.Path) is null ? Unreadable : KeptUnread));
			}
			else
			{
				var entry = new ManifestEntry(f.Path, f.Size, hash);
				entries.Add(entry);
				var synced = _known.TryGetValue(f.Path, out var known) && known == entry;
				var failure = synced ? null : BackedOff(entry);
				files.Add(new SyncFile(f.Path, f.Size, hash, synced ? FileSyncState.Synced : failure is null ? FileSyncState.Pending : FileSyncState.Error, failure));
			}
		}

		// What the walk could not see at all: unreadable files and folders it could not list (their known files are listed as kept).
		var unlisted = skipped.Where(s => s.EndsWith('/')).ToList();
		foreach (var path in skipped.Where(s => !s.EndsWith('/') && !rules.IsIgnored(s) && SyncPath.IsValid(s)))
		{
			var hash = Keep(path);
			files.Add(new SyncFile(path, hash is null ? 0 : _known[path].Size, hash, FileSyncState.Error, hash is null ? Unreadable : KeptUnread));
		}

		var scanned = scan.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
		foreach (var prefix in unlisted.Where(d => !rules.IsIgnored(d) && SyncPath.GetKeepError(d) is null))
		{
			keep.Add(prefix);
			var hidden = _known.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal) && k.Value.Sha256.Length > 0 && !scanned.Contains(k.Key) && !rules.IsIgnored(k.Key));
			foreach (var (path, known) in hidden)
			{
				files.Add(new SyncFile(path, known.Size, known.Sha256, FileSyncState.Error, KeptUnread));
			}
		}

		if (entries.Count == 0 && keep.Count == 0 && _known.Count > 0)
		{
			Problem = LooksEmpty;
			return null;
		}

		files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
		entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
		if (!files.SequenceEqual(_files))
		{
			SetFiles(files);
		}

		if (!unlisted.SequenceEqual(_unlistedDirectories))
		{
			_unlistedDirectories = unlisted;
			_errors = null;
		}

		Raise();
		return new ScanResult(entries, keep);
	}

	/// <returns>The repo name the server uses (sanitized folder name).</returns>
	private async Task<string> OpenAsync(int generation, CancellationToken ct)
	{
		var opened = await RequestAsync(MessageTypes.SyncOpen, new SyncOpenPayload(FolderName!), ct);
		EnsureFolder(generation);
		_repo = Read<SyncOpenPayload>(opened).Repo;
		return _repo;
	}

	private async Task<List<string>> SendManifestAsync(string repo, int generation, List<ManifestEntry> entries, IReadOnlySet<string> keep, CancellationToken ct)
	{
		var need = new List<string>();
		foreach (var page in ManifestPlanner.ManifestPages(repo, entries, [.. keep.Order(StringComparer.Ordinal)]))
		{
			EnsureCurrent(generation, repo);
			try
			{
				need.AddRange(Read<SyncNeedPayload>(await RequestAsync(MessageTypes.SyncManifest, page, ct)).Paths);
			}
			catch (RequestFailedException ex) when (_repo == repo && generation == _generation && ex.Code is ErrorCodes.TooLarge or ErrorCodes.BadRequest)
			{
				// The session was just opened (and not lost to a reconnect since) and the server refused the folder itself;
				// anything else (internal error, timeout) is transient and retried on the next pass.
				Blocked = true;
				throw;
			}
		}

		return need;
	}

	/// <summary>Never lists a path in both upserts and deletes (<see cref="ManifestPlanner.DeltaPages"/> puts them on separate pages).</summary>
	private async Task<List<string>> SendDeltaAsync(string repo, int generation, List<SyncDeltaPayload> pages, CancellationToken ct)
	{
		var need = new List<string>();
		foreach (var page in pages)
		{
			EnsureCurrent(generation, repo);
			need.AddRange(Read<SyncNeedPayload>(await RequestAsync(MessageTypes.SyncDelta, page, ct)).Paths);
		}

		return need;
	}

	/// <summary>Stops the cycle (it ends with <see cref="PassAborted"/>, or silently after a folder change) when it no longer runs in its session.</summary>
	private void EnsureCurrent(int generation, string repo)
	{
		EnsureFolder(generation);
		if (_repo != repo || transport.State != TransportState.Connected)
		{
			throw new InvalidOperationException(PassAborted);
		}
	}

	/// <summary>Stops a cycle started for an earlier folder before it writes anything.</summary>
	private void EnsureFolder(int generation)
	{
		if (generation != _generation)
		{
			throw new InvalidOperationException("The folder was changed.");
		}
	}

	/// <summary>The failure of an upload of this exact size and hash that must not be retried yet; null when it may be uploaded.</summary>
	private string? BackedOff(ManifestEntry entry) =>
		_failures.TryGetValue(entry.Path, out var failure) && failure.Entry == entry && time.GetUtcNow() < failure.Until ? failure.Error : null;

	/// <summary>
	/// Sequential uploads; a file that fails (refused, changed or unreadable) is marked and sent again on the next scan only,
	/// a lost connection ends the cycle.
	/// </summary>
	/// <returns>The stored entries and the failed files as "path: reason".</returns>
	private async Task<(List<ManifestEntry> Uploaded, List<string> Failed)> UploadAsync(string repo, int generation, List<ManifestEntry> entries, CancellationToken ct)
	{
		var uploaded = new List<ManifestEntry>();
		var failed = new List<string>();
		UploadDone = 0;
		UploadTotal = entries.Count;
		Phase = SyncPhase.Uploading;
		Raise();
		foreach (var entry in entries)
		{
			// A reconnect or a folder change ends the pass here: the remaining files are not attempted.
			EnsureCurrent(generation, repo);
			try
			{
				await UploadFileAsync(repo, generation, entry, ct);
				_known[entry.Path] = entry;
				_failures.Remove(entry.Path);
				SetFileState(entry.Path, FileSyncState.Synced, null);
				uploaded.Add(entry);
			}
			catch (Exception ex) when (ex is RequestFailedException or IOException or JSException)
			{
				// The same size and hash waits FailureBackoff before it is tried again; the same failure is logged once.
				var repeated = _failures.TryGetValue(entry.Path, out var earlier) && earlier.Entry == entry && earlier.Error == ex.Message;
				_failures[entry.Path] = (entry, ex.Message, time.GetUtcNow() + FailureBackoff);
				SetFileState(entry.Path, FileSyncState.Error, ex.Message);
				if (!repeated)
				{
					failed.Add($"{entry.Path}: {ex.Message}");
				}
			}

			UploadDone++;
			if (time.GetElapsedTime(_lastRaise) >= ProgressInterval)
			{
				Raise();
			}
		}

		return (uploaded, failed);
	}

	private async Task UploadFileAsync(string repo, int generation, ManifestEntry entry, CancellationToken ct)
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
			EnsureCurrent(generation, repo);
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
		_errors = null;
		(_syncedCount, _syncedBytes, _syncableCount, _fileErrorCount) = (0, 0, 0, 0);
		foreach (var file in _files)
		{
			Tally(file, 1);
		}
	}

	/// <summary>No-op for a path no longer listed (a folder change replaced the list while the cycle ran).</summary>
	private void SetFileState(string path, FileSyncState state, string? error)
	{
		if (_fileIndex.TryGetValue(path, out var i) && _files[i] is var file && (file.State != state || file.Error != error))
		{
			_files[i] = file with { State = state, Error = error };
			Tally(file, -1);
			Tally(_files[i], 1);
			if (file.State == FileSyncState.Error || state == FileSyncState.Error)
			{
				_errors = null;
			}
		}
	}

	/// <summary>Adds (<paramref name="sign"/> 1) or removes (-1) a file's share of the cached counts.</summary>
	private void Tally(SyncFile file, int sign)
	{
		var synced = file.State == FileSyncState.Synced ? sign : 0;
		_syncedCount += synced;
		_syncedBytes += synced * file.Size;
		_syncableCount += file.State == FileSyncState.TooLarge ? 0 : sign;
		_fileErrorCount += file.State == FileSyncState.Error ? sign : 0;
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

	/// <param name="Entries">What the server should have, sorted by path.</param>
	/// <param name="Keep">
	/// What the scan could not sync but the mirror must keep (never deleted, never uploaded): unreadable and too large files, and
	/// folders that could not be listed as a prefix ending in <c>/</c>. Only valid protocol paths.
	/// </param>
	private sealed record ScanResult(List<ManifestEntry> Entries, HashSet<string> Keep);
}
