using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Sync;

/// <summary>Section <c>Mirror</c>: where synced folders are mirrored (one sub-folder per repo).</summary>
public sealed class MirrorOptions
{
	public const string Section = "Mirror";

	/// <summary>Absolute after <see cref="ResolveRoot"/>; empty in configuration means the default.</summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>The configured root made absolute; by default <c>&lt;DataDir&gt;\mirror</c> (service) or <c>data\mirror</c> under the content root (dev, gitignored).</summary>
	public static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured, contentRoot);
		}

		return dataDir is not null ? dataDir.Mirror : Path.Combine(contentRoot, "data", "mirror");
	}
}
