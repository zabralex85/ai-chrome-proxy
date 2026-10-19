using System.Text;
using AiChromeProxy.Client.Packages;
using AiChromeProxy.Client.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>In-memory <see cref="IPackageFolder"/>: a picked NuGet cache whose files are a dictionary (paths relative to the cache).</summary>
public sealed class FakePackageFolder : IPackageFolder
{
	public string Name { get; set; } = "packages";

	public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

	/// <summary>Sizes <see cref="ListAsync"/> reports instead of the real ones (a file that changed after the listing).</summary>
	public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);

	/// <summary>What <see cref="PickAsync"/> returns; null = the user cancelled.</summary>
	public string? PickResult { get; set; } = "packages";

	/// <summary>Whether <see cref="RestoreAsync"/> finds a remembered folder.</summary>
	public bool Remembered { get; set; }

	public bool Granted { get; set; } = true;

	/// <summary>When set, every read throws it (the folder was deleted or access was revoked).</summary>
	public Exception? ReadFailure { get; set; }

	public bool Forgotten { get; private set; }

	public void Write(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

	public Task<string?> PickAsync()
	{
		if (PickResult is not null)
		{
			Name = PickResult;
			Remembered = true;
			Granted = true;
		}

		return Task.FromResult(PickResult);
	}

	public Task<FolderGrant?> RestoreAsync() => Task.FromResult(Remembered ? new FolderGrant(Name, Granted) : null);

	public Task<bool> RequestAccessAsync()
	{
		Granted = Remembered;
		return Task.FromResult(Granted);
	}

	public Task ForgetAsync()
	{
		Forgotten = true;
		Remembered = false;
		return Task.CompletedTask;
	}

	public Task<string?> ReadTextAsync(string path)
	{
		Check();
		return Task.FromResult(Files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : null);
	}

	public Task<IReadOnlyList<FileMeta>?> ListAsync(string folder)
	{
		Check();
		var prefix = folder + "/";
		IReadOnlyList<FileMeta> files = [.. Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
			.Select(f => new FileMeta(f.Key[prefix.Length..], Sizes.TryGetValue(f.Key, out var size) ? size : f.Value.Length, 0))];
		return Task.FromResult(files.Count == 0 ? null : files);
	}

	public Task<byte[]> ReadChunkAsync(string path, long offset, int length)
	{
		Check();
		return Task.FromResult(Files[path].Skip((int)offset).Take(length).ToArray());
	}

	private void Check()
	{
		if (!Granted)
		{
			throw new JSException("NotAllowedError: no access.");
		}

		if (ReadFailure is not null)
		{
			throw ReadFailure;
		}
	}
}
