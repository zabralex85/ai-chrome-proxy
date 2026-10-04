using System.Collections.Concurrent;
using System.Security.Cryptography;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Sync;

/// <summary><see cref="IMirrorStore"/> on the local file system under <see cref="MirrorOptions.Root"/>.</summary>
public sealed class FileSystemMirrorStore(IOptions<MirrorOptions> options) : IMirrorStore
{
	// AttributesToSkip replaces the default (Hidden | System): hidden files are synced files too; links are never entered.
	private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

	private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root));
	private readonly ConcurrentDictionary<string, CachedHash> _hashes = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// <paramref name="repoRoot"/> joined with <paramref name="path"/>, refused unless Win32 normalization leaves it exactly as joined (ignoring case):
	/// inside the repo folder, nothing collapsed, trimmed or expanded. A second line of defence behind <see cref="SyncPath"/>.
	/// </summary>
	public static string FullPathIn(string repoRoot, string path)
	{
		var full = Path.GetFullPath(Path.Combine(repoRoot, path));
		if (!full.StartsWith(repoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw BadRequest($"Path '{path}' resolves outside the mirror.");
		}

		if (!string.Equals(full, Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
		{
			throw BadRequest($"Path '{path}' is rewritten by the file system.");
		}

		return full;
	}

	public async Task<string?> GetHashAsync(string repo, string path, CancellationToken ct)
	{
		var file = RegularFile(repo, path);
		if (file is null)
		{
			return null;
		}

		var key = Key(repo, path);
		if (_hashes.TryGetValue(key, out var cached) && cached.Size == file.Length && cached.Written == file.LastWriteTimeUtc)
		{
			return cached.Sha256;
		}

		// Shares writers and deleters: a file another process has open must not fail the whole manifest page.
		using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

			// Cached only if the file did not change while it was read, so the key never pairs with a mixed hash.
			if (stream.Length == file.Length && File.GetLastWriteTimeUtc(stream.SafeFileHandle) == file.LastWriteTimeUtc)
			{
				_hashes[key] = new CachedHash(file.Length, file.LastWriteTimeUtc, hash);
			}

			return hash;
		}
	}

	public async Task<byte[]?> ReadAsync(string repo, string path, long offset, int count, CancellationToken ct)
	{
		var file = RegularFile(repo, path);
		if (file is null)
		{
			return null;
		}

		using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			stream.Position = Math.Min(offset, stream.Length);
			var buffer = new byte[Math.Min(count, stream.Length - stream.Position)];
			var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
			return read == buffer.Length ? buffer : buffer[..read];
		}
	}

	public long GetSize(string repo, string path) => RegularFile(repo, path)?.Length ?? 0;

	public string? RepoFolder(string repo)
	{
		var repoRoot = RepoRoot(repo);
		return Directory.Exists(repoRoot) && !IsLink(repoRoot) ? repoRoot : null;
	}

	public IReadOnlyList<string> ListFiles(string repo) => [.. Files(repo)];

	public bool HasFiles(string repo, Func<string, bool> excluded) => Files(repo).Any(p => !excluded(p));

	public Stream CreateTemp(string repo, string path, string tag)
	{
		var full = Resolve(repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);

		// A hard link or file link left at the temp name would be written through: remove the entry (never its target), then create anew.
		var temp = Temp(full, tag);
		File.Delete(temp);
		return new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
	}

	public void Commit(string repo, string path, string tag)
	{
		var full = Resolve(repo, path);
		File.Move(Temp(full, tag), full, overwrite: true);
		_hashes.TryRemove(Key(repo, path), out _);
	}

	public void DiscardTemp(string repo, string path, string tag) => File.Delete(Temp(Resolve(repo, path), tag));

	public void DeleteStaleTemps(string repo, string? keep, string tag)
	{
		var repoRoot = RepoRoot(repo);
		if (!Directory.Exists(repoRoot) || IsLink(repoRoot))
		{
			return;
		}

		var kept = keep is null ? null : Temp(Resolve(repo, keep), tag);
		foreach (var temp in Directory.EnumerateFiles(repoRoot, "*" + SyncPath.TempSuffix, Recursive))
		{
			if (string.Equals(temp, kept, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			try
			{
				File.Delete(temp);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Still open by another connection's upload (or read-only): left for a later sweep.
			}
		}
	}

	public void Delete(string repo, string path)
	{
		var full = Resolve(repo, path);
		if (File.Exists(full) && !IsLink(full))
		{
			File.Delete(full);
		}

		_hashes.TryRemove(Key(repo, path), out _);

		var repoRoot = RepoRoot(repo);
		for (var dir = Path.GetDirectoryName(full)!;
			dir.Length > repoRoot.Length && Directory.Exists(dir) && !IsLink(dir) && !Directory.EnumerateFileSystemEntries(dir).Any();
			dir = Path.GetDirectoryName(dir)!)
		{
			Directory.Delete(dir);
		}
	}

	private static string Key(string repo, string path) => repo + "/" + path;

	/// <summary><c>&lt;path&gt;.&lt;tag&gt;.aicp-tmp</c>: one per session, so a new session never collides with the open temp file of an old one.</summary>
	private static string Temp(string full, string tag) => full + "." + tag + SyncPath.TempSuffix;

	private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

	private string RepoRoot(string repo) =>
		RepoName.IsValid(repo) ? Path.Combine(_root, repo) : throw BadRequest("Invalid repo name.");

	/// <summary>The repo's regular files as protocol paths, enumerated lazily; links and junctions are skipped.</summary>
	private IEnumerable<string> Files(string repo)
	{
		var repoRoot = RepoRoot(repo);
		if (!Directory.Exists(repoRoot) || IsLink(repoRoot))
		{
			return [];
		}

		return Directory.EnumerateFiles(repoRoot, "*", Recursive)
			.Select(f => Path.GetRelativePath(repoRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
			.Where(SyncPath.IsValid);
	}

	/// <summary>The mirror file at a protocol path; null when it is missing, a folder or a link.</summary>
	private FileInfo? RegularFile(string repo, string path)
	{
		var file = new FileInfo(Resolve(repo, path));
		return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? file : null;
	}

	/// <summary>The absolute path for a protocol path: valid, inside the repo folder, and no link or junction on the way (the repo folder included).</summary>
	private string Resolve(string repo, string path)
	{
		if (SyncPath.GetError(path) is { } error)
		{
			throw BadRequest(error);
		}

		var repoRoot = RepoRoot(repo);
		var full = FullPathIn(repoRoot, path);
		for (var dir = Path.GetDirectoryName(full)!; dir.Length >= repoRoot.Length; dir = Path.GetDirectoryName(dir)!)
		{
			// Path.Exists, not Directory.Exists: a file-type link to a folder is not a directory but still redirects the path.
			if (Path.Exists(dir) && IsLink(dir))
			{
				throw BadRequest($"Path '{path}' crosses a link or junction in the mirror.");
			}
		}

		return full;
	}

	private sealed record CachedHash(long Size, DateTime Written, string Sha256);
}
