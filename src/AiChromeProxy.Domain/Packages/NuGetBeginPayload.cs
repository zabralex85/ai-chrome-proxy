namespace AiChromeProxy.Domain.Packages;

/// <summary>
/// <c>packages.nuget.begin</c>: starts uploading one package of <paramref name="FileCount"/> files, <paramref name="TotalSize"/> bytes in all (each file's path and
/// size come with its first chunk, so this message stays small); reply <c>packages.nuget.begun</c>.
/// </summary>
public sealed record NuGetBeginPayload(string Id, string Version, string ContentHash, int FileCount, long TotalSize);
