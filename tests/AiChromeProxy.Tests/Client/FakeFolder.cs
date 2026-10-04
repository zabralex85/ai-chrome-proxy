using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Client.Navigator;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain.Sync;
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

	/// <summary>What <see cref="IsSupportedAsync"/> answers: false like a browser without the File System Access API.</summary>
	public bool Supported { get; set; } = true;

	/// <summary>Thrown by <see cref="PickAsync"/>, <see cref="RequestAccessAsync"/> and <see cref="RequestWriteAccessAsync"/> (e.g. a policy refusing the picker).</summary>
	public Exception? ClickFailure { get; set; }

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

	/// <summary>Folders that exist without files (empty ones); a folder with files exists anyway.</summary>
	public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

	/// <summary>Whether files can be moved natively (<c>FileSystemHandle.move</c>); without it a rename copies, then removes the old file.</summary>
	public bool FileMove { get; set; } = true;

	/// <summary>Whether folders can be renamed (<c>FileSystemHandle.move</c>).</summary>
	public bool FolderMove { get; set; } = true;

	/// <summary>Thrown by <see cref="CreateFileAsync"/>, <see cref="CreateFolderAsync"/>, <see cref="RenameAsync"/> and <see cref="DeleteAsync"/> (e.g. the browser refusing).</summary>
	public Exception? OperationFailure { get; set; }

	/// <summary>Makes the copy of a rename without <see cref="FileMove"/> fail while writing: the partial new file is removed, the old one stays.</summary>
	public bool CopyFailure { get; set; }

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

	public Task<bool> IsSupportedAsync() => Task.FromResult(Supported);

	public Task<string?> PickAsync() => ClickFailure is null ? Task.FromResult(PickResult) : Task.FromException<string?>(ClickFailure);

	public Task<FolderGrant?> RestoreAsync() => Task.FromResult(Remembered ? new FolderGrant(Name, Granted) : null);

	public Task<bool> HasAccessAsync() => Task.FromResult(AccessAnswers.TryDequeue(out var answer) ? answer : Granted);

	public Task<bool> RequestAccessAsync()
	{
		if (ClickFailure is not null)
		{
			return Task.FromException<bool>(ClickFailure);
		}

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
		var directories = AllDirectories()
			.Where(d => !Skipped(d + "/x") && !unlisted.Any(u => d.StartsWith(u, StringComparison.Ordinal)))
			.ToList();
		return new FolderScan(files, Truncated, [.. Unreadable, .. unlisted, .. PartlyListedDirectories.Select(d => d + "/")], directories);
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

	public Task<FileBytes?> ReadFileAsync(string path)
	{
		// Like fsaccess.ts: invalid segments throw before anything is read; a missing file is null; at most MaxBytes + 1 bytes come back.
		if (path.Split('/').Any(s => s is "" or "." or ".." || s.Contains('\\')))
		{
			return Task.FromException<FileBytes?>(new JSException($"Invalid path '{path}'."));
		}

		if (HashFailures.Contains(path))
		{
			return Task.FromException<FileBytes?>(new JSException($"NotReadableError: '{path}' could not be read."));
		}

		if (SizeOnly.TryGetValue(path, out var size) && !Files.ContainsKey(path))
		{
			return Task.FromResult<FileBytes?>(new FileBytes(new byte[Math.Min(size, FileText.MaxBytes + 1L)], size));
		}

		return Task.FromResult(Files.TryGetValue(path, out var b) && !Unreadable.Contains(path)
			? new FileBytes(b.Take(FileText.MaxBytes + 1).ToArray(), b.Length)
			: null);
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
		if (ClickFailure is not null)
		{
			return Task.FromException<bool>(ClickFailure);
		}

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

	public Task DeleteAsync(string path, bool recursive = false)
	{
		if (!WriteAccess)
		{
			return Task.FromException(new JSException($"NotAllowedError: '{path}' cannot be deleted."));
		}

		if (OperationFailure is not null)
		{
			return Task.FromException(OperationFailure);
		}

		if (Invalid(path))
		{
			return Fail($"Invalid path '{path}'.");
		}

		if (AllDirectories().Contains(path))
		{
			if (!recursive)
			{
				return Fail($"'{path}' is a folder.");
			}

			var prefix = path + "/";
			foreach (var file in Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
			{
				Files.Remove(file);
			}

			foreach (var file in SizeOnly.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
			{
				SizeOnly.Remove(file);
			}

			Directories.RemoveWhere(d => d == path || d.StartsWith(prefix, StringComparison.Ordinal));
			Writes.Add(path);
			return Task.CompletedTask;
		}

		if (Files.Remove(path))
		{
			Writes.Add(path);
		}

		return Task.CompletedTask;
	}

	public Task CreateFileAsync(string path)
	{
		var refusal = Refusal(path, forCreate: true);
		if (refusal is not null)
		{
			return Task.FromException(refusal);
		}

		Files[path] = [];
		Writes.Add(path);
		return Task.CompletedTask;
	}

	public Task CreateFolderAsync(string path)
	{
		var refusal = Refusal(path, forCreate: true);
		if (refusal is not null)
		{
			return Task.FromException(refusal);
		}

		Directories.Add(path);
		Writes.Add(path);
		return Task.CompletedTask;
	}

	public Task RenameAsync(string path, string newName)
	{
		var refusal = Refusal(path, forCreate: false);
		if (refusal is not null)
		{
			return Task.FromException(refusal);
		}

		if (Invalid(newName) || newName.Contains('/'))
		{
			return Fail($"Invalid name '{newName}'.");
		}

		var isFolder = AllDirectories().Contains(path);
		var isFile = Files.ContainsKey(path) || SizeOnly.ContainsKey(path);
		if (!isFolder && !isFile)
		{
			return Fail($"'{path}' is not there.");
		}

		if (newName == Last(path))
		{
			return Task.CompletedTask;
		}

		if (isFolder && !FolderMove)
		{
			return Fail("This browser cannot rename folders.");
		}

		var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] + "/" : string.Empty;
		var target = parent + newName;
		if (Siblings(parent).Any(s => string.Equals(s, newName, StringComparison.OrdinalIgnoreCase) && s != Last(path)))
		{
			return Fail($"'{newName}' already exists here.");
		}

		if (isFolder)
		{
			var prefix = path + "/";
			Move(Files, prefix, target + "/");
			Move(SizeOnly, prefix, target + "/");
			var moved = Directories.Where(d => d == path || d.StartsWith(prefix, StringComparison.Ordinal)).ToList();
			Directories.ExceptWith(moved);
			Directories.UnionWith(moved.Select(d => target + d[path.Length..]));
		}
		else
		{
			if (!FileMove && SizeOnly.TryGetValue(path, out var size) && size > SyncLimits.MaxFileSize)
			{
				return Fail($"'{path}' is larger than 20 MB; this browser cannot rename it.");
			}

			if (!FileMove && CopyFailure)
			{
				return Fail($"NotAllowedError: '{target}' cannot be written.");
			}

			Move(Files, path, target);
			Move(SizeOnly, path, target);
		}

		Writes.Add(path);
		Writes.Add(target);
		return Task.CompletedTask;
	}

	public Task<bool> CanRenameFoldersAsync() => Task.FromResult(FolderMove);

	/// <summary>Counts the files under the folder, at most <paramref name="maxEntries"/> (files only here: a simplification of the real walk, which also counts folders).</summary>
	public Task<int> CountFilesAsync(string path, int maxEntries)
	{
		if (Invalid(path) || !AllDirectories().Contains(path))
		{
			return Task.FromException<int>(new JSException($"NotFoundError: '{path}' is not there."));
		}

		var prefix = path + "/";
		var count = Files.Keys.Concat(SizeOnly.Keys).Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
		return Task.FromResult(Math.Min(count, maxEntries));
	}

	public Task WatchVisibilityAsync(Action<bool> changed)
	{
		Visibility = changed;
		return Task.CompletedTask;
	}

	private static bool Invalid(string path) => path.Split('/').Any(s => s is "" or "." or ".." || s.Contains('\\'));

	private static string Last(string path) => path[(path.LastIndexOf('/') + 1)..];

	private static Task Fail(string message) => Task.FromException(new JSException(message));

	private static void Move<T>(Dictionary<string, T> map, string from, string to)
	{
		foreach (var key in map.Keys.Where(k => k == from || (from.EndsWith('/') && k.StartsWith(from, StringComparison.Ordinal))).ToList())
		{
			var value = map[key];
			map.Remove(key);
			map[to + key[from.Length..]] = value;
		}
	}

	/// <summary>Every folder: the empty ones and the folders of the files.</summary>
	private HashSet<string> AllDirectories()
	{
		var all = new HashSet<string>(Directories, StringComparer.Ordinal);
		foreach (var path in Files.Keys.Concat(SizeOnly.Keys).Concat(Directories.Select(d => d + "/x")))
		{
			for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
			{
				all.Add(path[..slash]);
			}
		}

		return all;
	}

	/// <summary>Names inside the folder (<paramref name="parent"/> is empty or ends with <c>/</c>).</summary>
	private IEnumerable<string> Siblings(string parent) =>
		Files.Keys.Concat(SizeOnly.Keys).Concat(AllDirectories())
			.Where(k => k.Length > parent.Length && k.StartsWith(parent, StringComparison.Ordinal) && !k[parent.Length..].Contains('/'))
			.Select(k => k[parent.Length..]);

	/// <summary>The failure a create, rename or delete of <paramref name="path"/> meets first, like the browser: no write access, a forced failure, an invalid path, a missing folder or a taken name.</summary>
	private JSException? Refusal(string path, bool forCreate)
	{
		if (!WriteAccess)
		{
			return new JSException($"NotAllowedError: '{path}' cannot be changed.");
		}

		if (OperationFailure is not null)
		{
			return new JSException(OperationFailure.Message);
		}

		if (Invalid(path))
		{
			return new JSException($"Invalid path '{path}'.");
		}

		if (!forCreate)
		{
			return null;
		}

		var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
		if (parent.Length > 0 && !AllDirectories().Contains(parent))
		{
			return new JSException($"NotFoundError: '{parent}' is not there.");
		}

		return Siblings(parent.Length == 0 ? string.Empty : parent + "/").Any(s => string.Equals(s, Last(path), StringComparison.OrdinalIgnoreCase))
			? new JSException($"'{path}' already exists.")
			: null;
	}
}
