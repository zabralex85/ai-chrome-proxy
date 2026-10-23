using System.Text;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Sync;

/// <summary>What the server never deletes, pushes or serves of a repo: the mirror's <c>.gitignore</c> and the project's extra excludes.</summary>
public static class MirrorRules
{
	public static async Task<IgnoreRules> LoadAsync(IMirrorStore store, IProjectStore projects, string repo, CancellationToken ct)
	{
		var gitignore = await store.ReadAsync(repo, ".gitignore", 0, (int)SyncLimits.MaxFileSize, ct);
		return IgnoreRules.Create(gitignore is null ? null : Encoding.UTF8.GetString(gitignore), projects.GetSettings(repo).Excludes);
	}
}
