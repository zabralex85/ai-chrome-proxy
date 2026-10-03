using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Projects;

/// <summary>Section <c>Projects</c>: where the per-repo SQLite database lives.</summary>
public sealed class ProjectsOptions
{
	public const string Section = "Projects";

	/// <summary>Absolute after <see cref="ResolveDatabase"/>; empty in configuration means the default.</summary>
	public string Database { get; set; } = string.Empty;

	/// <summary>The configured path made absolute; by default <c>&lt;DataDir&gt;\aicp.db</c> (service) or <c>data\aicp.db</c> under the content root (dev, gitignored).</summary>
	public static string ResolveDatabase(string? configured, DataDirectory? dataDir, string contentRoot)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured, contentRoot);
		}

		return dataDir is not null ? dataDir.Database : Path.Combine(contentRoot, "data", "aicp.db");
	}
}
