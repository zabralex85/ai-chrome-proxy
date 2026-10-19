namespace AiChromeProxy.Domain.Packages;

/// <summary><c>packages.nuget.stored</c>: the package is on the server.</summary>
public sealed record NuGetStoredPayload(string Id, string Version);
