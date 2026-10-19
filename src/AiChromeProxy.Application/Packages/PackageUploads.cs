using System.Collections.Concurrent;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Packages;

/// <summary>
/// <c>packages.nuget.*</c>: which private packages the server lacks, and one upload per connection at a time, written to a temp folder and put in place
/// only when the files <c>begin</c> announced (count and total size) all arrived and the <c>.nupkg</c> matches its <c>contentHash</c>. Each file is checked
/// at its first chunk, which carries its path and size. A chunk that fails ends the upload: later chunks are dropped and the
/// last one gets that first error. Leftover temp folders (a restart) are deleted when the service is created.
/// </summary>
public sealed class PackageUploads
{
	public const string ChangedMessage = "Changed while uploading; retried on the next pass.";

	private const string TooLargeMessage = "A package may have at most 2 000 files and 250 MB.";

	private readonly IPackageStore _store;
	private readonly ILogger<PackageUploads> _logger;
	private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);

	public PackageUploads(IPackageStore store, ILogger<PackageUploads> logger)
	{
		_store = store;
		_logger = logger;
		try
		{
			var deleted = _store.DeleteTemps();
			if (deleted > 0)
			{
				_logger.LogInformation("Deleted {Count} unfinished package uploads", deleted);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Could not delete unfinished package uploads");
		}
	}

	public NuGetMissingPayload Have(NuGetHavePayload payload)
	{
		var packages = payload.Packages ?? [];
		if (packages.Count > PackageLimits.MaxHavePackages)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"At most {PackageLimits.MaxHavePackages} packages at once.");
		}

		if (packages.Any(p => p is null))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "packages.nuget.have has an empty entry.");
		}

		return new NuGetMissingPayload([.. packages.Select(p => Key(p.Id, p.Version, p.ContentHash)).Distinct().Where(k => !_store.HasNuGet(k.Id, k.Version))]);
	}

	public NuGetBegunPayload Begin(string connectionId, NuGetBeginPayload payload)
	{
		var key = Key(payload.Id, payload.Version, payload.ContentHash);
		if (payload.FileCount > PackageLimits.MaxPackageFiles || payload.TotalSize > PackageLimits.MaxPackageBytes)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, TooLargeMessage);
		}

		if (payload.FileCount < 1 || payload.TotalSize < 0)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "packages.nuget.begin needs at least one file and a size of 0 or more.");
		}

		Close(connectionId);
		var id = Guid.NewGuid().ToString("N");
		var upload = new Upload(id, key, payload.ContentHash, payload.FileCount, payload.TotalSize, _store.Begin(key.Id, key.Version, id[..8]));
		_uploads[connectionId] = upload;
		return new NuGetBegunPayload(id);
	}

	public NuGetStoredPayload? Chunk(string connectionId, NuGetChunkPayload payload)
	{
		if (!_uploads.TryGetValue(connectionId, out var upload) || upload.Id != payload.UploadId)
		{
			return Unknown(payload);
		}

		lock (upload)
		{
			if (upload.Closed)
			{
				return Unknown(payload);
			}

			if (upload.Error is { } error)
			{
				return payload.Last ? throw Finish(connectionId, upload, error) : null;
			}

			try
			{
				return Append(connectionId, upload, payload);
			}
			catch (Exception ex) when (ex is EnvelopeException or IOException or UnauthorizedAccessException or ArgumentException)
			{
				var failure = ex as EnvelopeException ?? new EnvelopeException(ErrorCodes.Internal, "Could not store the package.");
				if (ex is not EnvelopeException)
				{
					_logger.LogWarning(ex, "Storing package {Id} {Version} failed", upload.Key.Id, upload.Key.Version);
				}

				upload.Error = failure;
				return payload.Last ? throw Finish(connectionId, upload, failure) : null;
			}
		}
	}

	public void Close(string connectionId)
	{
		if (_uploads.TryRemove(connectionId, out var upload))
		{
			lock (upload)
			{
				upload.Closed = true;
				Discard(upload);
			}
		}
	}

	private static NuGetStoredPayload? Unknown(NuGetChunkPayload payload) =>
		payload.Last ? throw new EnvelopeException(ErrorCodes.NotFound, "Unknown package upload; it starts again on the next pass.") : null;

	private static NuGetPackageKey Key(string? id, string? version, string? hash)
	{
		if (!NuGetNames.IsValidId(id) || !NuGetNames.IsValidVersion(version) || !NuGetNames.IsValidContentHash(hash))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "Invalid package id, version or contentHash.");
		}

		return new NuGetPackageKey(id!.ToLowerInvariant(), version!.ToLowerInvariant());
	}

	/// <summary>A file's first chunk: its path and size are checked against the rules and what <c>begin</c> announced.</summary>
	private static void Start(Upload upload, NuGetChunkPayload payload)
	{
		if (payload.Offset != 0 || payload.Size is not { } size)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Chunk of '{payload.Path}' at {payload.Offset} is out of order (a file starts at 0 with its size).");
		}

		if (upload.Paths.Count == upload.FileCount)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"More files than the {upload.FileCount} of packages.nuget.begin.");
		}

		if (size < 0 || SyncPath.GetError(payload.Path) is not null || !upload.Paths.Add(payload.Path))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Invalid or repeated file '{payload.Path}'.");
		}

		if (size > upload.TotalSize - upload.Started)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"The files are larger than the {upload.TotalSize} bytes of packages.nuget.begin.");
		}

		upload.HasNupkg |= string.Equals(payload.Path, upload.Nupkg, StringComparison.Ordinal);
		upload.Current = payload.Path;
		upload.Size = size;
		upload.Started += size;
		upload.Received = 0;
	}

	private NuGetStoredPayload? Append(string connectionId, Upload upload, NuGetChunkPayload payload)
	{
		if (upload.Current is null)
		{
			Start(upload, payload);
		}
		else if (!string.Equals(payload.Path, upload.Current, StringComparison.Ordinal) || payload.Offset != upload.Received || payload.Size is not null)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Chunk of '{payload.Path}' at {payload.Offset} is out of order.");
		}

		var path = upload.Current!;
		byte[] data;
		try
		{
			data = SyncData.Decode(payload.Data ?? string.Empty);
		}
		catch (FormatException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The chunk is not base64url.");
		}

		if (data.Length > PackageLimits.ChunkSize || upload.Received + data.Length > upload.Size)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{path}' is longer than its size {upload.Size}.");
		}

		_store.Append(upload.Temp, path, data, create: upload.Received == 0);
		upload.Received += data.Length;
		if (upload.Received == upload.Size)
		{
			upload.Complete++;
			upload.Current = null;
		}

		if (!payload.Last)
		{
			return null;
		}

		if (upload.Current is not null || upload.Complete != upload.FileCount || upload.Started != upload.TotalSize)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The package arrived incomplete; it starts again on the next pass.");
		}

		if (!upload.HasNupkg)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"The package has no {upload.Nupkg}.");
		}

		if (_store.Sha512(upload.Temp, upload.Nupkg) != upload.ContentHash)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, ChangedMessage);
		}

		_store.Commit(upload.Temp, upload.Key.Id, upload.Key.Version);
		_uploads.TryRemove(new KeyValuePair<string, Upload>(connectionId, upload));
		return new NuGetStoredPayload(upload.Key.Id, upload.Key.Version);
	}

	/// <summary>A folder that cannot be deleted now is left for <see cref="IPackageStore.DeleteTemps"/> at the next start.</summary>
	private void Discard(Upload upload)
	{
		try
		{
			_store.Discard(upload.Temp);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Could not delete the unfinished upload of {Id} {Version}", upload.Key.Id, upload.Key.Version);
		}
	}

	private EnvelopeException Finish(string connectionId, Upload upload, EnvelopeException error)
	{
		_uploads.TryRemove(new KeyValuePair<string, Upload>(connectionId, upload));
		Discard(upload);
		return error;
	}

	private sealed class Upload(string id, NuGetPackageKey key, string contentHash, int fileCount, long totalSize, string temp)
	{
		public string Id { get; } = id;

		public NuGetPackageKey Key { get; } = key;

		public string ContentHash { get; } = contentHash;

		/// <summary>Gets the lower-case <c>&lt;id&gt;.&lt;version&gt;.nupkg</c> that must be among the files.</summary>
		public string Nupkg { get; } = $"{key.Id}.{key.Version}.nupkg";

		public int FileCount { get; } = fileCount;

		public long TotalSize { get; } = totalSize;

		public string Temp { get; } = temp;

		/// <summary>Gets the paths of the files started so far (no two equal ignoring case: the store is on Windows).</summary>
		public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Gets or sets the file being received, or null between files.</summary>
		public string? Current { get; set; }

		/// <summary>Gets or sets the size of <see cref="Current"/>.</summary>
		public long Size { get; set; }

		/// <summary>Gets or sets the bytes of <see cref="Current"/> received so far.</summary>
		public long Received { get; set; }

		/// <summary>Gets or sets the sum of the sizes of the files started so far.</summary>
		public long Started { get; set; }

		/// <summary>Gets or sets a value indicating whether <see cref="Nupkg"/> (exactly, lower case) is among the files.</summary>
		public bool HasNupkg { get; set; }

		/// <summary>Gets or sets how many files arrived whole.</summary>
		public int Complete { get; set; }

		public EnvelopeException? Error { get; set; }

		public bool Closed { get; set; }
	}
}
