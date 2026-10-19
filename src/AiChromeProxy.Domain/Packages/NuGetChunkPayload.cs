namespace AiChromeProxy.Domain.Packages;

/// <summary>
/// <c>packages.nuget.chunk</c>: part of a file of the upload, in the order of <c>begin</c> (a file of size 0 is one empty chunk);
/// <paramref name="Data"/> base64url without padding, ≤ <see cref="PackageLimits.ChunkSize"/> bytes raw. Only the package's <paramref name="Last"/>
/// chunk is answered: <c>packages.nuget.stored</c>, or the upload's first error.
/// </summary>
public sealed record NuGetChunkPayload(string UploadId, string Path, long Offset, string Data, bool Last);
