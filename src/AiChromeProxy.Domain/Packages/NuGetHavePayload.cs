namespace AiChromeProxy.Domain.Packages;

/// <summary><c>packages.nuget.have</c>: the private packages the open repo uses (at most <see cref="PackageLimits.MaxHavePackages"/>); reply <c>packages.nuget.missing</c>.</summary>
public sealed record NuGetHavePayload(IReadOnlyList<NuGetPackageInfo> Packages);
