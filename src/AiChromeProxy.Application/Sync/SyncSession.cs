using System.Security.Cryptography;
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>
/// One connection's sync state: the open repo, the paths of the manifest being received, the files requested with
/// <c>sync.need</c> and the upload in progress. Each path is decided with <see cref="SyncDecision"/> from the client's hash, the mirror's and
/// the base kept in <see cref="IProjectStore"/>; what the server changed is pushed (<c>sync.remote</c>) instead of being overwritten or deleted.
/// Messages are handled one at a time; <see cref="Dispose"/> (the connection closed) may run while a message is still being handled, and the upload that message leaves is then discarded when it finishes. Uploads are
/// sequential (ponytail: one upload at a time, parallelise if first syncs of large repos are too slow).
/// </summary>
public sealed partial class SyncSession(
	IMirrorStore store,
	IProjectStore projects,
	ILogger logger,
	TimeProvider time,
	Action<SyncSession, string?, string?>? repoChanged = null) : IDisposable
{
	private const string EmptyMirrorRefusal = "An empty folder would delete the whole mirror; refusing.";

	private readonly HashSet<string> _manifestPaths = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _keep = new(StringComparer.OrdinalIgnoreCase);
	/// <summary>Uploads asked for with <c>sync.need</c>, with the mirror's hash when they were decided (the upload may replace only that version).</summary>
	private readonly Dictionary<string, (ManifestEntry Entry, string? Mirror)> _expected = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly string _tag = Guid.NewGuid().ToString("N")[..SyncPath.TempTagLength];
	private readonly object _notify = new();
	private volatile bool _disposed;
	private string? _watchedRepo;
	private EnvelopeContext? _context;
	private string? _repo;
	private Upload? _upload;
	private (string Path, Exception Error)? _chunkFailure;
	private int _storedFiles;
	private long _storedBytes;

	/// <summary>When the pass being uploaded started (its first manifest page, or its delta): the stored summary measures that pass.</summary>
	private long _started;
	private int _passNeed;

	/// <summary>The repo this session has open (null before <c>sync.open</c>).</summary>
	public string? Repo => _repo;

	/// <summary>
	/// Handles one sync message; after <see cref="Dispose"/> messages are ignored (no reply). Pushes (<c>sync.remote</c>) go out through
	/// <paramref name="context"/>, which is also kept for <see cref="MirrorChangedAsync"/>.
	/// </summary>
	public async Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			_context = context;
			return _disposed ? null : await RouteAsync(request, ct);
		}
		finally
		{
			_gate.Release();
			DiscardIfDisposed();
		}
	}

	/// <summary>Discards the unfinished upload now, or as soon as the message being handled finishes.</summary>
	public void Dispose()
	{
		_disposed = true;
		NotifyRepo(null);
		DiscardIfDisposed();
	}

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

	private static T Read<T>(Envelope request)
		where T : class
	{
		try
		{
			return request.Payload.Deserialize<T>(JsonSerializerOptions.Web) ?? throw BadRequest($"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw BadRequest($"Invalid {request.Type} payload.");
		}
	}

	private static Envelope Reply<T>(string type, T payload, Envelope request) => Envelope.Create(type, payload, request.CorrelationId);

	private static void Validate(ManifestEntry? entry)
	{
		if (entry is null)
		{
			throw BadRequest("Manifest entry is null.");
		}

		if (SyncPath.GetError(entry.Path) is { } error)
		{
			throw BadRequest(error);
		}

		if (entry.Size < 0)
		{
			throw BadRequest($"'{entry.Path}' has a negative size.");
		}

		if (entry.Size > SyncLimits.MaxFileSize)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{entry.Path}' is larger than {SyncLimits.MaxFileSize} bytes.");
		}

		if (!IsSha256(entry.Sha256))
		{
			throw BadRequest($"'{entry.Path}' needs a SHA-256 as 64 lower-case hex characters.");
		}
	}

	private static bool IsSha256(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitLower);

	private static string CheckPath(string? path) => SyncPath.GetError(path) is { } error ? throw BadRequest(error) : path!;

	private async Task<Envelope?> RouteAsync(Envelope request, CancellationToken ct)
	{
		switch (request.Type)
		{
			case MessageTypes.SyncOpen:
				return Open(Read<SyncOpenPayload>(request), request);
			case MessageTypes.SyncManifest:
				return await ManifestAsync(Read<SyncManifestPayload>(request), request, ct);
			case MessageTypes.SyncDelta:
				return await DeltaAsync(Read<SyncDeltaPayload>(request), request, ct);
			case MessageTypes.SyncChunk:
				SyncChunkPayload? chunk = null;
				try
				{
					chunk = Read<SyncChunkPayload>(request);
					return await ChunkAsync(chunk, request, ct);
				}
				catch (Exception ex)
				{
					// Nobody waits for the reply to a chunk that is not the last: its failure is kept for the last chunk.
					DiscardUpload();
					_chunkFailure = chunk is { Last: false, Path: { } path } ? (path, ex) : null;
					throw;
				}

			case MessageTypes.SyncFetch:
				return await FetchAsync(Read<SyncFetchPayload>(request), request, ct);
			case MessageTypes.SyncAck:
				return await AckAsync(Read<SyncAckPayload>(request), request, ct);
			default:
				throw new EnvelopeException(ErrorCodes.UnknownType, $"Not a sync message: {request.Type}");
		}
	}

	/// <summary>Tells the owner which repo this session watches now (null: none, disposed); ignored once disposed, so a close never leaks a watch.</summary>
	private void NotifyRepo(string? repo)
	{
		lock (_notify)
		{
			if (_watchedRepo == repo || (_disposed && repo is not null))
			{
				return;
			}

			var from = _watchedRepo;
			_watchedRepo = repo;
			repoChanged?.Invoke(this, from, repo);
		}
	}

	private Envelope Open(SyncOpenPayload payload, Envelope request)
	{
		var repo = RepoName.Sanitize(payload.Repo) ?? throw BadRequest("sync.open needs the folder name in 'repo'.");
		DiscardUpload();
		_chunkFailure = null;
		EndManifestPass();
		_expected.Clear();
		_repo = repo;
		NotifyRepo(repo);
		ResetStats();
		return Reply(MessageTypes.SyncOpened, new SyncOpenPayload(repo, projects.GetSettings(repo)), request);
	}

	private async Task<Envelope> ManifestAsync(SyncManifestPayload payload, Envelope request, CancellationToken ct)
	{
		try
		{
			var repo = CheckRepo(payload.Repo);
			if (_manifestPaths.Count + _keep.Count == 0)
			{
				// The first page starts a pass.
				ResetStats();
			}

			var entries = payload.Entries ?? throw BadRequest("sync.manifest needs 'entries'.");
			var keep = payload.Keep ?? [];
			if (_manifestPaths.Count + entries.Count > SyncLimits.MaxFiles || _keep.Count + keep.Count > SyncLimits.MaxFiles)
			{
				throw new EnvelopeException(ErrorCodes.TooLarge, $"A manifest may list (and keep) at most {SyncLimits.MaxFiles} files.");
			}

			entries.ToList().ForEach(Validate);
			if (keep.Select(SyncPath.GetKeepError).FirstOrDefault(e => e is not null) is { } keepError)
			{
				throw BadRequest(keepError);
			}

			var batch = await BeginAsync(repo, ct);
			var need = await NeedAsync(batch, entries, ct);
			_manifestPaths.UnionWith(entries.Select(e => e.Path));
			_keep.UnionWith(keep);
			_passNeed += need.Count;
			if (payload.Final)
			{
				// Case-insensitive: the mirror is on Windows, where "Readme.md" on disk is the manifest's "README.md".
				// What the browser could not read, list or sync (keep) and what is excluded (server-only files such as bin/) stays as it is.
				var files = store.ListFiles(repo);
				var stale = files.Where(p => !_manifestPaths.Contains(p) && !SyncPath.IsKept(p, _keep) && !batch.Rules.IsIgnored(p));
				var deletes = await DecideAbsentAsync(batch, stale, ct);
				if (_manifestPaths.Count + _keep.Count == 0 && deletes.Count > 0)
				{
					// A browser that lost access to the folder must never wipe the mirror.
					throw BadRequest(EmptyMirrorRefusal);
				}

				Delete(batch, deletes);

				// Bases of paths that neither side has any more.
				var onMirror = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
				foreach (var path in batch.Bases.Keys.Where(p => !_manifestPaths.Contains(p) && !onMirror.Contains(p)))
				{
					batch.BaseChanges[path] = null;
				}

				store.DeleteStaleTemps(repo, _upload?.Entry.Path, _tag);
				logger.LogInformation(
					"Sync {Repo}: manifest of {Files} files, {Need} to upload, {Deleted} deleted",
					repo,
					_manifestPaths.Count,
					_passNeed,
					deletes.Count);
				SaveBases(batch);
				if (!batch.Baselined)
				{
					projects.SetBaselined(repo);
				}

				EndManifestPass();
			}

			await FinishAsync(batch, ct);
			return Reply(MessageTypes.SyncNeed, new SyncNeedPayload(repo, need), request);
		}
		catch
		{
			// A refused page ends the pass; the client starts the next one from the first page.
			EndManifestPass();
			throw;
		}
	}

	private async Task<Envelope> DeltaAsync(SyncDeltaPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		ResetStats();
		var upserts = payload.Upserts ?? throw BadRequest("sync.delta needs 'upserts'.");
		var deletes = payload.Deletes ?? throw BadRequest("sync.delta needs 'deletes'.");

		// All paths are checked before anything changes, so a refused delta leaves the mirror as it was.
		deletes.ToList().ForEach(p => CheckPath(p));
		upserts.ToList().ForEach(Validate);
		var deleted = new HashSet<string>(deletes, StringComparer.OrdinalIgnoreCase);
		if (upserts.FirstOrDefault(e => deleted.Contains(e.Path)) is { } both)
		{
			throw BadRequest($"'{both.Path}' is both upserted and deleted.");
		}

		// Like the empty-manifest guard: a delta may not leave a non-empty mirror with nothing synced (stored or awaited); excluded files do not count.
		var batch = await BeginAsync(repo, ct);
		if (upserts.Count == 0
			&& deleted.Count > 0
			&& store.ListFiles(repo).Where(p => !batch.Rules.IsIgnored(p)).ToList() is { Count: > 0 } stored
			&& stored.Concat(_expected.Keys).All(deleted.Contains))
		{
			throw BadRequest(EmptyMirrorRefusal);
		}

		var need = await NeedAsync(batch, upserts, ct);
		deletes.ToList().ForEach(p => _expected.Remove(p));
		Delete(batch, await DecideAbsentAsync(batch, deletes.Where(p => !batch.Rules.IsIgnored(p)), ct));
		await FinishAsync(batch, ct);
		return Reply(MessageTypes.SyncNeed, new SyncNeedPayload(repo, need), request);
	}

	/// <summary>
	/// Returns the paths of the (validated) entries the client changed; those are expected as chunks. Entries equal to the mirror get
	/// their base, entries the server changed (or both did) are pushed instead. Refuses when more than <see cref="SyncLimits.MaxFiles"/>
	/// uploads would be pending, before anything changes.
	/// </summary>
	private async Task<List<string>> NeedAsync(Batch batch, IReadOnlyList<ManifestEntry> entries, CancellationToken ct)
	{
		var same = new List<string>();
		var changed = new List<(ManifestEntry Entry, string? Mirror)>();
		foreach (var entry in entries)
		{
			var mirror = await store.GetHashAsync(batch.Repo, entry.Path, ct);
			var baseHash = batch.Bases.GetValueOrDefault(entry.Path);
			switch (SyncDecision.Decide(entry.Sha256, mirror, baseHash, batch.Baselined))
			{
				case SyncAction.Upload:
					changed.Add((entry, mirror));
					if (!batch.Baselined && baseHash != mirror)
					{
						// Before the baseline the mirror's version counts as agreed: an upload still pending when the connection drops is
						// then a client change on the next (baselined) pass, not a conflict.
						batch.BaseChanges[entry.Path] = mirror;
					}

					break;
				case SyncAction.InSync:
					same.Add(entry.Path);
					if (baseHash != entry.Sha256)
					{
						batch.BaseChanges[entry.Path] = entry.Sha256;
					}

					break;
				default:
					// The server changed it (or both did): the client decides. An upload it asked for earlier must not overwrite it either.
					same.Add(entry.Path);
					AddPush(batch, entry.Path, mirror, baseHash);
					break;
			}
		}

		if (_expected.Count + changed.Select(e => e.Entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(p => !_expected.ContainsKey(p)) > SyncLimits.MaxFiles)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"At most {SyncLimits.MaxFiles} files may wait for upload.");
		}

		same.ForEach(p => _expected.Remove(p));
		changed.ForEach(e => _expected[e.Entry.Path] = e);
		return [.. changed.Select(e => e.Entry.Path)];
	}

	private async Task<Envelope?> ChunkAsync(SyncChunkPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		if (payload.Path is null || payload.Data is null)
		{
			throw BadRequest("sync.chunk needs 'path' and 'data'.");
		}

		// The rest of an upload that already failed is dropped; its last chunk gets the first failure (offset 0 starts over).
		if (_chunkFailure is { } failure && failure.Path == payload.Path && payload.Offset != 0)
		{
			return payload.Last ? throw failure.Error : null;
		}

		_chunkFailure = null;

		if (!_expected.TryGetValue(payload.Path, out var expected))
		{
			throw new EnvelopeException(ErrorCodes.NotFound, $"'{payload.Path}' was not requested with sync.need (or is already stored).");
		}

		var entry = expected.Entry;

		byte[] data;
		try
		{
			data = SyncData.Decode(payload.Data);
		}
		catch (FormatException)
		{
			throw BadRequest($"Chunk of '{entry.Path}' is not base64url.");
		}

		if (data.Length > SyncLimits.ChunkSize)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"Chunks carry at most {SyncLimits.ChunkSize} bytes.");
		}

		if (payload.Offset == 0)
		{
			DiscardUpload();
			_upload = new Upload(entry, store.CreateTemp(repo, entry.Path, _tag));
		}
		else if (_upload is null || _upload.Entry.Path != entry.Path || _upload.Received != payload.Offset)
		{
			throw BadRequest($"Chunk of '{entry.Path}' at offset {payload.Offset} is out of order.");
		}

		var upload = _upload!;
		if (upload.Received + data.Length > entry.Size)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{entry.Path}' is longer than its manifest size {entry.Size}.");
		}

		await upload.Stream.WriteAsync(data, ct);
		upload.Hash.AppendData(data);
		upload.Received += data.Length;
		if (!payload.Last)
		{
			return null;
		}

		var hash = Convert.ToHexStringLower(upload.Hash.GetHashAndReset());
		await upload.Stream.DisposeAsync();
		if (upload.Received != entry.Size || hash != entry.Sha256 || (payload.Sha256 is not null && payload.Sha256 != hash))
		{
			throw BadRequest($"'{entry.Path}' arrived with a different size or hash than in its manifest; it is requested again on the next scan.");
		}

		// The server may have changed the file since the upload was decided: do not overwrite that (the next pass decides it again).
		// ponytail: a write landing between this check and Commit (milliseconds) is still overwritten; lock the target during the move if that ever matters.
		if (await store.GetHashAsync(repo, entry.Path, ct) is var mirror && mirror != expected.Mirror)
		{
			_expected.Remove(entry.Path);
			var batch = await BeginAsync(repo, ct);
			var baseHash = batch.Bases.GetValueOrDefault(entry.Path);
			if (mirror != baseHash)
			{
				AddPush(batch, entry.Path, mirror, baseHash);
			}

			await FinishAsync(batch, ct);
			throw BadRequest($"'{entry.Path}' changed on the server during its upload; it is decided again on the next scan.");
		}

		store.Commit(repo, entry.Path, _tag);

		// Before the reply, so the watcher event of this very write finds the mirror equal to the base and pushes nothing.
		projects.SetBases(repo, [new(entry.Path, entry.Sha256)]);
		upload.Dispose();
		_upload = null;
		_expected.Remove(entry.Path);
		_storedFiles++;
		_storedBytes += entry.Size;
		if (_expected.Count == 0)
		{
			logger.LogInformation(
				"Sync {Repo}: stored {Files} files, {Bytes} bytes in {Duration}",
				repo,
				_storedFiles,
				_storedBytes,
				time.GetElapsedTime(_started));
			ResetStats();
		}

		return Reply(MessageTypes.SyncStored, new SyncStoredPayload(repo, entry.Path), request);
	}

	private string CheckRepo(string? repo)
	{
		if (_repo is null)
		{
			throw BadRequest("Send sync.open first.");
		}

		return repo == _repo ? _repo : throw BadRequest($"Repo '{repo}' is not the open one ('{_repo}').");
	}

	private void EndManifestPass()
	{
		_manifestPaths.Clear();
		_keep.Clear();
		_passNeed = 0;
	}

	private void ResetStats()
	{
		_storedFiles = 0;
		_storedBytes = 0;
		_started = time.GetTimestamp();
	}

	/// <summary>After <see cref="Dispose"/>: discards the upload unless a message is being handled (that message does it when it finishes).</summary>
	private void DiscardIfDisposed()
	{
		if (!_disposed || !_gate.Wait(0))
		{
			return;
		}

		try
		{
			DiscardUpload();
		}
		finally
		{
			_gate.Release();
		}
	}

	private void DiscardUpload()
	{
		if (_upload is null)
		{
			return;
		}

		_upload.Dispose();
		store.DiscardTemp(_repo!, _upload.Entry.Path, _tag);
		_upload = null;
	}

	private sealed class Upload(ManifestEntry entry, Stream stream) : IDisposable
	{
		public ManifestEntry Entry => entry;

		public Stream Stream => stream;

		public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		public long Received { get; set; }

		public void Dispose()
		{
			stream.Dispose();
			Hash.Dispose();
		}
	}
}
