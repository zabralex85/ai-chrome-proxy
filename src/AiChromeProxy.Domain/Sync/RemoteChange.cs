namespace AiChromeProxy.Domain.Sync;

/// <summary>A file the server changed in the mirror: <c>Sha256</c> null = deleted; <c>Base</c> = the hash the server last had in sync (null = it had none).</summary>
public sealed record RemoteChange(string Path, string? Sha256, long Size, string? Base);
