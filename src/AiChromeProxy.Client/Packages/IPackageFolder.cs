using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Client.Packages;

/// <summary>The NuGet cache folder picked in this browser (<c>Scripts/pkgaccess.ts</c>); paths are relative to it, with forward slashes.</summary>
public interface IPackageFolder
{
	/// <summary>Shows the folder picker and remembers the folder; its name, or null when the picker was cancelled.</summary>
	Task<string?> PickAsync();

	/// <summary>The remembered folder, or null.</summary>
	Task<FolderGrant?> RestoreAsync();

	/// <summary>Asks the browser for read access again (must run from a click).</summary>
	Task<bool> RequestAccessAsync();

	/// <summary>Forgets the remembered folder.</summary>
	Task ForgetAsync();

	/// <summary>Text of a file, or null when it is missing.</summary>
	Task<string?> ReadTextAsync(string path);

	/// <summary>Every file under <paramref name="folder"/>, paths relative to it; null when the folder is missing.</summary>
	Task<IReadOnlyList<FileMeta>?> ListAsync(string folder);

	/// <summary>Bytes [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>) of a file (fewer at its end).</summary>
	Task<byte[]> ReadChunkAsync(string path, long offset, int length);
}
