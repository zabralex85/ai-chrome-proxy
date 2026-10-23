namespace AiChromeProxy.Domain.Projects;

/// <summary><c>repos {repos, truncated}</c>: server → client, the reply to <c>repos.list</c>; most recent activity first, cut off (<paramref name="Truncated"/>) to stay under the hub's limit.</summary>
public sealed record ReposPayload(IReadOnlyList<RepoInfo> Repos, bool Truncated);
