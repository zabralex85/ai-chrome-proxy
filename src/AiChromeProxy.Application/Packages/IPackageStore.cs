namespace AiChromeProxy.Application.Packages;

/// <summary>
/// The server's NuGet packages: <c>&lt;root&gt;\nuget\&lt;id&gt;\&lt;version&gt;\</c> (lower case, checked by the caller), filled through a temp folder
/// <c>&lt;version&gt;.&lt;tag&gt;.tmp</c> that is renamed into place only when complete. Paths leaving their folder throw <see cref="ArgumentException"/>.
/// </summary>
public interface IPackageStore
{
	/// <summary>Absolute <c>&lt;root&gt;\nuget</c> (it may not exist yet).</summary>
	string NuGetFolder { get; }

	bool HasNuGet(string id, string version);

	/// <summary>Creates an empty temp folder for the package; returns its handle for the calls below.</summary>
	string Begin(string id, string version, string tag);

	/// <summary>Creates the file (and its folders) when <paramref name="data"/> starts it, else appends; <paramref name="path"/> is relative with forward slashes.</summary>
	void Append(string upload, string path, byte[] data, bool create);

	/// <summary>Base64 sha512 of a file of the upload.</summary>
	string Sha512(string upload, string path);

	/// <summary>Renames the temp folder into place; false (and the temp folder deleted) when the package is there already.</summary>
	bool Commit(string upload, string id, string version);

	/// <summary>Deletes the temp folder; no error when it is gone.</summary>
	void Discard(string upload);

	/// <summary>Deletes every temp folder (a restart lost their uploads); returns how many.</summary>
	int DeleteTemps();
}
