namespace AiChromeProxy.Domain.Packages;

/// <summary><c>packages.nuget.begin</c>: starts uploading one package (its files in the order they will be sent); reply <c>packages.nuget.begun</c>.</summary>
public sealed record NuGetBeginPayload(string Id, string Version, string ContentHash, IReadOnlyList<NuGetFileInfo> Files);
