namespace AiChromeProxy.Domain.Packages;

/// <summary>A package by lower-case id and version.</summary>
public sealed record NuGetPackageKey(string Id, string Version);
