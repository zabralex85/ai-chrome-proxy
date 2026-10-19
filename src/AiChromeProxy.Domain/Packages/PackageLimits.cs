namespace AiChromeProxy.Domain.Packages;

/// <summary>Limits of the <c>packages.nuget.*</c> messages.</summary>
public static class PackageLimits
{
	/// <summary>Largest package (all its files), in bytes.</summary>
	public const long MaxPackageBytes = 250L * 1024 * 1024;

	/// <summary>Most files in one package folder.</summary>
	public const int MaxPackageFiles = 2_000;

	/// <summary>Most packages in one <c>packages.nuget.have</c>.</summary>
	public const int MaxHavePackages = 2_000;

	/// <summary>Largest chunk before encoding, in bytes.</summary>
	public const int ChunkSize = 16 * 1024;
}
