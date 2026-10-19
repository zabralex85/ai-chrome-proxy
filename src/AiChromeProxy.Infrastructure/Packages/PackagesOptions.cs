using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Packages;

/// <summary>Section <c>Packages</c>: where the private NuGet packages uploaded from the browser's computer are kept (outside the mirror).</summary>
public sealed class PackagesOptions
{
	public const string Section = "Packages";

	/// <summary>Absolute after <see cref="ResolveRoot"/>; empty in configuration means the default.</summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>The configured root made absolute; by default <c>&lt;DataDir&gt;\packages</c> (service) or <c>data\packages</c> under the content root (dev, gitignored).</summary>
	public static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured, contentRoot);
		}

		return dataDir is not null ? dataDir.Packages : Path.Combine(contentRoot, "data", "packages");
	}
}
