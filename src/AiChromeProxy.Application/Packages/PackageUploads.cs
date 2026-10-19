using System.Collections.Concurrent;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Packages;

/// <summary>
/// <c>packages.nuget.*</c>: which private packages the server lacks, and one upload per connection at a time, written to a temp folder and put in place
/// only when every file arrived and the <c>.nupkg</c> matches its <c>contentHash</c>. A chunk that fails ends the upload: later chunks are dropped and the
/// last one gets that first error. Leftover temp folders (a restart) are deleted when the service is created.
/// </summary>
public sealed class PackageUploads
{
	public const string ChangedMessage = "Changed while uploading; retried on the next pass.";

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
		var files = payload.Files ?? [];
		if (files.Any(f => f is null))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "packages.nuget.begin has an empty file entry.");
		}

		if (files.Count > PackageLimits.MaxPackageFiles || files.Any(f => f.Size > PackageLimits.MaxPackageBytes) || files.Sum(f => Math.Max(0, f.Size)) > PackageLimits.MaxPackageBytes)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"A package may have at most {PackageLimits.MaxPackageFiles} files and 250 MB.");
		}

		if (files.Any(f => f.Size < 0 || SyncPath.GetError(f.Path) is not null) || files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "packages.nuget.begin has an invalid or repeated file path.");
		}

		var nupkg = $"{key.Id}.{key.Version}.nupkg";
		if (!files.Any(f => string.Equals(f.Path, nupkg, StringComparison.Ordinal)))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"packages.nuget.begin must list {nupkg}.");
		}

		Close(connectionId);
		var id = Guid.NewGuid().ToString("N");
		var upload = new Upload(id, key, payload.ContentHash, nupkg, files, _store.Begin(key.Id, key.Version, id[..8]));
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

	private NuGetStoredPayload? Append(string connectionId, Upload upload, NuGetChunkPayload payload)
	{
		if (upload.Index >= upload.Files.Count)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "More chunks than the package's files.");
		}

		var file = upload.Files[upload.Index];
		if (!string.Equals(payload.Path, file.Path, StringComparison.Ordinal) || payload.Offset != upload.Received)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Chunk of '{payload.Path}' at {payload.Offset} is out of order.");
		}

		byte[] data;
		try
		{
			data = SyncData.Decode(payload.Data ?? string.Empty);
		}
		catch (FormatException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The chunk is not base64url.");
		}

		if (data.Length > PackageLimits.ChunkSize || upload.Received + data.Length > file.Size)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{file.Path}' is longer than its size {file.Size}.");
		}

		_store.Append(upload.Temp, file.Path, data, create: upload.Received == 0);
		upload.Received += data.Length;
		if (upload.Received == file.Size)
		{
			upload.Index++;
			upload.Received = 0;
		}

		if (!payload.Last)
		{
			return null;
		}

		if (upload.Index != upload.Files.Count)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The package arrived incomplete; it starts again on the next pass.");
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

	private sealed class Upload(string id, NuGetPackageKey key, string contentHash, string nupkg, IReadOnlyList<NuGetFileInfo> files, string temp)
	{
		public string Id { get; } = id;

		public NuGetPackageKey Key { get; } = key;

		public string ContentHash { get; } = contentHash;

		public string Nupkg { get; } = files.First(f => string.Equals(f.Path, nupkg, StringComparison.OrdinalIgnoreCase)).Path;

		public IReadOnlyList<NuGetFileInfo> Files { get; } = files;

		public string Temp { get; } = temp;

		public int Index { get; set; }

		public long Received { get; set; }

		public EnvelopeException? Error { get; set; }

		public bool Closed { get; set; }
	}
}
