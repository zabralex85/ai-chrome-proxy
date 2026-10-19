namespace AiChromeProxy.Domain.Packages;

/// <summary>A package the browser found in its NuGet cache: lower-case id and version, and the base64 sha512 of its <c>.nupkg</c> (<c>.nupkg.metadata</c> <c>contentHash</c>).</summary>
public sealed record NuGetPackageInfo(string Id, string Version, string ContentHash);
