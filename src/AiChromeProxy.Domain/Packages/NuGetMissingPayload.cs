namespace AiChromeProxy.Domain.Packages;

/// <summary><c>packages.nuget.missing</c>: those of <c>have</c> the server does not hold.</summary>
public sealed record NuGetMissingPayload(IReadOnlyList<NuGetPackageKey> Missing);
