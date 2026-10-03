using System.Security.Cryptography;
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>
/// One connection's sync state: the open repo, the paths of the manifest being received, the files requested with
/// <c>sync.need</c> and the upload in progress. SignalR runs one hub invocation per connection at a time, so a session is never
/// used concurrently; uploads are sequential (ponytail: one upload at a time, parallelise if first syncs of large repos are too slow).
/// </summary>
public sealed class SyncSession(IMirrorStore store, ILogger logger, TimeProvider time) : IDisposable
{
	private readonly HashSet<string> _manifestPaths = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ManifestEntry> _expected = new(StringComparer.Ordinal);
	private string? _repo;
	private Upload? _upload;
	private int _storedFiles;
	private long _storedBytes;
	private long _started;

	public async Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct)
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
				try
				{
					return await ChunkAsync(Read<SyncChunkPayload>(request), request, ct);
				}
				catch
				{
					DiscardUpload();
					throw;
				}

			default:
				throw new EnvelopeException(ErrorCodes.UnknownType, $"Not a sync message: {request.Type}");
		}
	}

	public void Dispose() => DiscardUpload();

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

		if (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(char.IsAsciiHexDigitLower))
		{
			throw BadRequest($"'{entry.Path}' needs a SHA-256 as 64 lower-case hex characters.");
		}
	}

	private Envelope Open(SyncOpenPayload payload, Envelope request)
	{
		var repo = RepoName.Sanitize(payload.Repo) ?? throw BadRequest("sync.open needs the folder name in 'repo'.");
		DiscardUpload();
		_manifestPaths.Clear();
		_expected.Clear();
		_repo = repo;
		ResetStats();
		return Reply(MessageTypes.SyncOpened, new SyncOpenPayload(repo), request);
	}

	private async Task<Envelope> ManifestAsync(SyncManifestPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		var entries = payload.Entries ?? throw BadRequest("sync.manifest needs 'entries'.");
		if (_manifestPaths.Count + entries.Count > SyncLimits.MaxFiles)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"A manifest may list at most {SyncLimits.MaxFiles} files.");
		}

		var need = await NeedAsync(repo, entries, ct);
		_manifestPaths.UnionWith(entries.Select(e => e.Path));
		if (payload.Final)
		{
			// Case-insensitive: the mirror is on Windows, where "Readme.md" on disk is the manifest's "README.md".
			var stale = store.ListFiles(repo).Where(p => !_manifestPaths.Contains(p)).ToList();
			stale.ForEach(p => store.Delete(repo, p));
			logger.LogInformation(
				"Sync {Repo}: manifest of {Files} files, {Need} to upload, {Deleted} deleted",
				repo,
				_manifestPaths.Count,
				_expected.Count,
				stale.Count);
			_manifestPaths.Clear();
		}

		return Reply(MessageTypes.SyncNeed, new SyncNeedPayload(repo, need), request);
	}

	private async Task<Envelope> DeltaAsync(SyncDeltaPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		var upserts = payload.Upserts ?? throw BadRequest("sync.delta needs 'upserts'.");
		var deletes = payload.Deletes ?? throw BadRequest("sync.delta needs 'deletes'.");

		// All paths are checked before anything changes, so a refused delta leaves the mirror as it was.
		foreach (var path in deletes)
		{
			if (SyncPath.GetError(path) is { } error)
			{
				throw BadRequest(error);
			}
		}

		var need = await NeedAsync(repo, upserts, ct);
		foreach (var path in deletes)
		{
			store.Delete(repo, path);
			_expected.Remove(path);
		}

		return Reply(MessageTypes.SyncNeed, new SyncNeedPayload(repo, need), request);
	}

	/// <summary>Validates the entries and returns the paths whose mirror content differs; those are expected as chunks.</summary>
	private async Task<List<string>> NeedAsync(string repo, IReadOnlyList<ManifestEntry> entries, CancellationToken ct)
	{
		entries.ToList().ForEach(Validate);
		var need = new List<string>();
		foreach (var entry in entries)
		{
			if (await store.GetHashAsync(repo, entry.Path, ct) == entry.Sha256)
			{
				_expected.Remove(entry.Path);
				continue;
			}

			_expected[entry.Path] = entry;
			need.Add(entry.Path);
		}

		return need;
	}

	private async Task<Envelope?> ChunkAsync(SyncChunkPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		if (payload.Path is null || payload.Data is null)
		{
			throw BadRequest("sync.chunk needs 'path' and 'data'.");
		}

		if (!_expected.TryGetValue(payload.Path, out var entry))
		{
			throw new EnvelopeException(ErrorCodes.NotFound, $"'{payload.Path}' was not requested with sync.need (or is already stored).");
		}

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
			_upload = new Upload(entry, store.CreateTemp(repo, entry.Path));
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

		store.Commit(repo, entry.Path);
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

	private void ResetStats()
	{
		_storedFiles = 0;
		_storedBytes = 0;
		_started = time.GetTimestamp();
	}

	private void DiscardUpload()
	{
		if (_upload is null)
		{
			return;
		}

		_upload.Dispose();
		store.DiscardTemp(_repo!, _upload.Entry.Path);
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
