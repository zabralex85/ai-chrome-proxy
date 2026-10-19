using System.Security.Cryptography;
using AiChromeProxy.Application.Packages;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Packages;

/// <summary><see cref="IPackageStore"/> on the file system under <see cref="PackagesOptions.Root"/>.</summary>
public sealed class FileSystemPackageStore(IOptions<PackagesOptions> options) : IPackageStore
{
	/// <summary>Temp folders start with this; a NuGet version (<c>[A-Za-z0-9.+-]</c>) cannot.</summary>
	private const string TempMark = "~";

	private readonly string _nuget = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root)), "nuget");

	public string NuGetFolder => _nuget;

	public bool HasNuGet(string id, string version) => Directory.Exists(Package(id, version));

	public string Begin(string id, string version, string tag)
	{
		var folder = Child(_nuget, id);
		Child(folder, version);
		var temp = Child(folder, TempMark + version + TempMark + tag);
		if (Directory.Exists(temp))
		{
			Directory.Delete(temp, recursive: true);
		}

		Directory.CreateDirectory(temp);
		return temp;
	}

	public void Append(string upload, string path, byte[] data, bool create)
	{
		var file = Inside(Temp(upload), path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		using (var stream = new FileStream(file, create ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
		{
			stream.Write(data);
		}
	}

	public string Sha512(string upload, string path)
	{
		using (var stream = File.OpenRead(Inside(Temp(upload), path)))
		{
			return Convert.ToBase64String(SHA512.HashData(stream));
		}
	}

	public bool Commit(string upload, string id, string version)
	{
		var temp = Temp(upload);
		var target = Package(id, version);
		if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(temp)), Path.GetFileName(Path.GetDirectoryName(target)), StringComparison.OrdinalIgnoreCase)
			|| !Path.GetFileName(temp).StartsWith(TempMark + version + TempMark, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The upload belongs to another package.", nameof(upload));
		}

		try
		{
			if (!Directory.Exists(target))
			{
				Directory.Move(temp, target);
				return true;
			}
		}
		catch (IOException) when (Directory.Exists(target))
		{
			// Another upload committed the same package between the check and the move: ours is the loser.
		}

		Directory.Delete(temp, recursive: true);
		return false;
	}

	public void Discard(string upload)
	{
		var temp = Temp(upload);
		if (Directory.Exists(temp))
		{
			Directory.Delete(temp, recursive: true);
		}
	}

	public int DeleteTemps()
	{
		if (!Directory.Exists(_nuget))
		{
			return 0;
		}

		var temps = Directory.EnumerateDirectories(_nuget).SelectMany(Directory.EnumerateDirectories).Where(d => Path.GetFileName(d).StartsWith(TempMark, StringComparison.Ordinal)).ToList();
		temps.ForEach(d => Directory.Delete(d, recursive: true));
		return temps.Count;
	}

	/// <summary>The folder <paramref name="segment"/> right under <paramref name="parent"/>; a name Windows would change (trailing dots or spaces) throws.</summary>
	private static string Child(string parent, string segment)
	{
		var full = Path.GetFullPath(Path.Combine(parent, segment));
		if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(full), segment, StringComparison.Ordinal))
		{
			throw new ArgumentException("The path leaves its folder.", nameof(segment));
		}

		return full;
	}

	private static string Inside(string folder, string relative)
	{
		var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (Path.IsPathRooted(relative) || !full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The path leaves the package folder.", nameof(relative));
		}

		return full;
	}

	private string Package(string id, string version) => Child(Child(_nuget, id), version);

	/// <summary>A handle from <see cref="Begin"/>: a temp folder two levels under the NuGet folder.</summary>
	private string Temp(string upload)
	{
		var full = Path.GetFullPath(upload);
		if (!Path.GetFileName(full).StartsWith(TempMark, StringComparison.Ordinal) || !string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(full)), _nuget, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Not an upload of this store.", nameof(upload));
		}

		return full;
	}
}
