namespace AiChromeProxy.Domain.Packages;

/// <summary>
/// Limits of the <c>packages.nuget.*</c> messages. Every message, inside its envelope, must stay under SignalR's 32 KB receive limit (the server drops the
/// connection otherwise): <c>have</c> is paged by serialized size like <c>sync.manifest</c> (<see cref="Sync.SyncPages"/>), <c>begin</c> carries counts only
/// and each file's path and size travel with its first chunk.
/// </summary>
public static class PackageLimits
{
	/// <summary>Largest package (all its files), in bytes.</summary>
	public const long MaxPackageBytes = 250L * 1024 * 1024;

	/// <summary>Most files in one package folder.</summary>
	public const int MaxPackageFiles = 2_000;

	/// <summary>Most packages in one <c>packages.nuget.have</c> (the browser sends smaller pages: <see cref="Sync.SyncPages"/>).</summary>
	public const int MaxHavePackages = 2_000;

	/// <summary>Largest chunk before encoding, in bytes.</summary>
	public const int ChunkSize = 16 * 1024;
}
