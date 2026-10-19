namespace AiChromeProxy.Domain.Packages;

/// <summary>A file of a package folder: path relative to <c>&lt;id&gt;/&lt;version&gt;/</c> (forward slashes) and its size.</summary>
public sealed record NuGetFileInfo(string Path, long Size);
