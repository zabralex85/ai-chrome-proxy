using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>Section <c>Attachments</c>: where files attached to chat messages are kept (one sub-folder per repo, outside the mirror).</summary>
public sealed class AttachmentsOptions
{
	public const string Section = "Attachments";

	/// <summary>Absolute after <see cref="ResolveRoot"/>; empty in configuration means the default.</summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>The configured root made absolute; by default <c>&lt;DataDir&gt;\attachments</c> (service) or <c>data\attachments</c> under the content root (dev, gitignored).</summary>
	public static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured, contentRoot);
		}

		return dataDir is not null ? dataDir.Attachments : Path.Combine(contentRoot, "data", "attachments");
	}
}
