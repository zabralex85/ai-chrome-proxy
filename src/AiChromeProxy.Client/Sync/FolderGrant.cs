namespace AiChromeProxy.Client.Sync;

/// <summary>A remembered folder and whether the browser still grants read access to it.</summary>
public sealed record FolderGrant(string Name, bool Granted);
