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

	/// <summary>Paths whose <see cref="ReadChunkAsync"/> fails from this offset on (after earlier chunks were read).</summary>
	public Dictionary<string, long> ReadFailuresAt { get; } = new(StringComparer.Ordinal);

	/// <summary>Answers <see cref="HasAccessAsync"/> gives first, one per call; <see cref="Granted"/> once empty.</summary>
	public Queue<bool> AccessAnswers { get; } = new();

	/// <summary>Files whose <c>getFile()</c> fails during the walk: left out of the scan and reported in <see cref="FolderScan.Skipped"/>, like fsaccess.js.</summary>
	public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

	/// <summary>Files the walk sees but that cannot be read afterwards: <see cref="HashAsync"/> returns null, <see cref="ReadTextAsync"/> throws.</summary>
	public HashSet<string> HashFailures { get; } = new(StringComparer.Ordinal);

	/// <summary>Folders (paths without a trailing <c>/</c>) whose listing fails: their files are left out, the prefix is reported as skipped.</summary>
	public HashSet<string> UnlistedDirectories { get; } = new(StringComparer.Ordinal);

	/// <summary>Folders whose listing stopped part-way: their files are still returned, but the prefix is reported as skipped too.</summary>
	public HashSet<string> PartlyListedDirectories { get; } = new(StringComparer.Ordinal);

	/// <summary>Path of every <see cref="ReadChunkAsync"/> call, in order.</summary>
	public List<string> ChunkReads { get; } = [];

	public int Scans { get; private set; }

	/// <summary>The directories the last <see cref="ScanAsync"/> was told to skip.</summary>
	public IReadOnlyList<string> SkippedDirectories { get; private set; } = [];

	public List<string> Hashed { get; } = [];

	public Action<bool>? Visibility { get; private set; }

	/// <summary>Whether the folder may be written; <see cref="RequestWriteAccessAsync"/> grants it. Writes without it throw like the browser.</summary>
	public bool WriteAccess { get; set; }

	/// <summary>Answers <see cref="HasWriteAccessAsync"/> gives first, one per call; <see cref="WriteAccess"/> once empty.</summary>
	public Queue<bool> WriteAccessAnswers { get; } = new();

	/// <summary>Path of every <see cref="WriteAsync"/> and <see cref="DeleteAsync"/> that changed the folder, in order.</summary>
	public List<string> Writes { get; } = [];

	public void Write(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

	public Task<string?> PickAsync() => Task.FromResult(PickResult);

	public Task<FolderGrant?> RestoreAsync() => Task.FromResult(Remembered ? new FolderGrant(Name, Granted) : null);

	public Task<bool> HasAccessAsync() => Task.FromResult(AccessAnswers.TryDequeue(out var answer) ? answer : Granted);

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

		// Like fsaccess.js: a root that cannot be listed throws (the engine checks access), it never looks empty.
		if (ScanFailure is not null)
		{
			throw ScanFailure;
		}

		// Like fsaccess.ts: a name is skipped at any depth, "/a/b" only at that path.
		SkippedDirectories = skipDirectories;
		var skip = skipDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
		bool Skipped(string path)
		{
			var segments = path.Split('/')[..^1];
			return segments.Where((name, i) => skip.Contains(name) || skip.Contains("/" + string.Join('/', segments[..(i + 1)]))).Any();
		}

		var unlisted = UnlistedDirectories.Select(d => d + "/").ToList();
		var files = Files.Select(f => new FileMeta(f.Key, f.Value.Length, 0))
			.Concat(SizeOnly.Select(f => new FileMeta(f.Key, f.Value, 0)))
			.Where(f => !Skipped(f.Path))
			.Where(f => !Unreadable.Contains(f.Path) && !unlisted.Any(d => f.Path.StartsWith(d, StringComparison.Ordinal)))
			.ToList();
		return new FolderScan(files, Truncated, [.. Unreadable, .. unlisted, .. PartlyListedDirectories.Select(d => d + "/")]);
	}

	public Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths)
	{
		Hashed.AddRange(paths);
		IReadOnlyList<string?> hashes = [.. paths.Select(p => Files.TryGetValue(p, out var b) && !HashFailures.Contains(p) ? Convert.ToHexStringLower(SHA256.HashData(b)) : null)];
		return Task.FromResult(hashes);
	}

	public Task<string?> ReadTextAsync(string path)
	{
		if (HashFailures.Contains(path))
		{
			return Task.FromException<string?>(new JSException($"NotReadableError: '{path}' could not be read."));
		}

		return Task.FromResult(Files.TryGetValue(path, out var b) && !Unreadable.Contains(path) ? Encoding.UTF8.GetString(b) : null);
	}

	public Task<byte[]> ReadChunkAsync(string path, long offset, int length)
	{
		ChunkReads.Add(path);
		if (ReadFailures.Contains(path) || (ReadFailuresAt.TryGetValue(path, out var at) && offset >= at))
		{
			return Task.FromException<byte[]>(new JSException($"NotReadableError: '{path}' could not be read."));
		}

		var content = ReadOverride.TryGetValue(path, out var o) ? o : Files[path];
		return Task.FromResult(content.Skip((int)offset).Take(length).ToArray());
	}

	public Task<string?> HashNowAsync(string path)
	{
		if (HashFailures.Contains(path))
		{
			return Task.FromException<string?>(new JSException($"NotReadableError: '{path}' could not be read."));
		}

		return Task.FromResult(Files.TryGetValue(path, out var b) ? Convert.ToHexStringLower(SHA256.HashData(b)) : null);
	}

	public Task<bool> HasWriteAccessAsync() => Task.FromResult(WriteAccessAnswers.TryDequeue(out var answer) ? answer : WriteAccess);

	public Task<bool> RequestWriteAccessAsync()
	{
		WriteAccess = true;
		return Task.FromResult(true);
	}

	public Task WriteAsync(string path, byte[] content)
	{
		if (!WriteAccess)
		{
			return Task.FromException(new JSException($"NotAllowedError: '{path}' cannot be written."));
		}

		Files[path] = content;
		Writes.Add(path);
		return Task.CompletedTask;
	}

	public Task DeleteAsync(string path)
	{
		if (!WriteAccess)
		{
			return Task.FromException(new JSException($"NotAllowedError: '{path}' cannot be deleted."));
		}

		if (Files.Remove(path))
		{
			Writes.Add(path);
		}

		return Task.CompletedTask;
	}

	public Task WatchVisibilityAsync(Action<bool> changed)
	{
		Visibility = changed;
		return Task.CompletedTask;
	}
}
