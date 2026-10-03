using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Client.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>In-memory <see cref="IFolderAccess"/>: a picked folder whose files are a dictionary.</summary>
public sealed class FakeFolder : IFolderAccess
{
	public string Name { get; set; } = "My Repo";

	public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

	/// <summary>Files reported by the walk with this size but no content (e.g. larger than the sync limit).</summary>
	public Dictionary<string, long> SizeOnly { get; } = new(StringComparer.Ordinal);

	/// <summary>What <see cref="PickAsync"/> returns; null = the user cancelled.</summary>
	public string? PickResult { get; set; } = "My Repo";

	/// <summary>Whether <see cref="RestoreAsync"/> finds a remembered folder.</summary>
	public bool Remembered { get; set; }

	public bool Granted { get; set; } = true;

	public Exception? ScanFailure { get; set; }

	public bool Truncated { get; set; }

	/// <summary>When set, <see cref="ScanAsync"/> waits for it (holds a scan in progress).</summary>
	public Task? ScanGate { get; set; }

	/// <summary>Bytes <see cref="ReadChunkAsync"/> returns instead of the real ones (simulates a file changing during upload).</summary>
	public Dictionary<string, byte[]> ReadOverride { get; } = new(StringComparer.Ordinal);

	/// <summary>Paths whose <see cref="ReadChunkAsync"/> fails like fsaccess.js does for a file that changed or vanished.</summary>
	public HashSet<string> ReadFailures { get; } = new(StringComparer.Ordinal);

	/// <summary>Path of every <see cref="ReadChunkAsync"/> call, in order.</summary>
	public List<string> ChunkReads { get; } = [];

	public int Scans { get; private set; }

	public List<string> Hashed { get; } = [];

	public Action<bool>? Visibility { get; private set; }

	public void Write(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

	public Task<string?> PickAsync() => Task.FromResult(PickResult);

	public Task<FolderGrant?> RestoreAsync() => Task.FromResult(Remembered ? new FolderGrant(Name, Granted) : null);

	public Task<bool> RequestAccessAsync()
	{
		Granted = true;
		return Task.FromResult(true);
	}

	public async Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries)
	{
		Scans++;
		if (ScanGate is not null)
		{
			await ScanGate;
		}

		if (ScanFailure is not null)
		{
			throw ScanFailure;
		}

		var skip = skipDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var files = Files.Select(f => new FileMeta(f.Key, f.Value.Length, 0))
			.Concat(SizeOnly.Select(f => new FileMeta(f.Key, f.Value, 0)))
			.Where(f => !f.Path.Split('/')[..^1].Any(skip.Contains))
			.ToList();
		return new FolderScan(files, Truncated);
	}

	public Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths)
	{
		Hashed.AddRange(paths);
		IReadOnlyList<string?> hashes = [.. paths.Select(p => Files.TryGetValue(p, out var b) ? Convert.ToHexStringLower(SHA256.HashData(b)) : null)];
		return Task.FromResult(hashes);
	}

	public Task<string?> ReadTextAsync(string path) =>
		Task.FromResult(Files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : null);

	public Task<byte[]> ReadChunkAsync(string path, long offset, int length)
	{
		ChunkReads.Add(path);
		if (ReadFailures.Contains(path))
		{
			return Task.FromException<byte[]>(new JSException($"NotReadableError: '{path}' could not be read."));
		}

		var content = ReadOverride.TryGetValue(path, out var o) ? o : Files[path];
		return Task.FromResult(content.Skip((int)offset).Take(length).ToArray());
	}

	public Task WatchVisibilityAsync(Action<bool> changed)
	{
		Visibility = changed;
		return Task.CompletedTask;
	}
}
