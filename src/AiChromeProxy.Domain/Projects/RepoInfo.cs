namespace AiChromeProxy.Domain.Projects;

/// <summary>
/// A project on the server (a folder of the mirror): <paramref name="LastActivity"/> is the newest update of its chat sessions (null: no chat yet),
/// <paramref name="Running"/> whether Claude is working there now, <paramref name="Open"/> whether a browser has the folder open now.
/// </summary>
public sealed record RepoInfo(string Name, DateTimeOffset? LastActivity, bool Running, bool Open);
