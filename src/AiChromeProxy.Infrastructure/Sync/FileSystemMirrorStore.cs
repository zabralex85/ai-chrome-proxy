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
		var file = new FileInfo(Resolve(repo, path));
		if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
		{
			return null;
		}

		var key = Key(repo, path);
		if (_hashes.TryGetValue(key, out var cached) && cached.Size == file.Length && cached.Written == file.LastWriteTimeUtc)
		{
			return cached.Sha256;
		}

		string hash;
		using (var stream = file.OpenRead())
		{
			hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
		}

		_hashes[key] = new CachedHash(file.Length, file.LastWriteTimeUtc, hash);
		return hash;
	}

	public IReadOnlyList<string> ListFiles(string repo)
	{
		var repoRoot = RepoRoot(repo);
		if (!Directory.Exists(repoRoot) || IsLink(repoRoot))
		{
			return [];
		}

		// AttributesToSkip replaces the default (Hidden | System): hidden files are synced files too; links are never entered.
		var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
		return Directory.EnumerateFiles(repoRoot, "*", enumeration)
			.Select(f => Path.GetRelativePath(repoRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
			.Where(SyncPath.IsValid)
			.ToList();
	}

	public Stream CreateTemp(string repo, string path)
	{
		var full = Resolve(repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		return new FileStream(full + SyncPath.TempSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
	}

	public void Commit(string repo, string path)
	{
		var full = Resolve(repo, path);
		File.Move(full + SyncPath.TempSuffix, full, overwrite: true);
		_hashes.TryRemove(Key(repo, path), out _);
	}

	public void DiscardTemp(string repo, string path) => File.Delete(Resolve(repo, path) + SyncPath.TempSuffix);

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

	private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

	private string RepoRoot(string repo) =>
		RepoName.IsValid(repo) ? Path.Combine(_root, repo) : throw BadRequest("Invalid repo name.");

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
			if (Directory.Exists(dir) && IsLink(dir))
			{
				throw BadRequest($"Path '{path}' crosses a link or junction in the mirror.");
			}
		}

		return full;
	}

	private sealed record CachedHash(long Size, DateTime Written, string Sha256);
}
