using System.Text.Json.Serialization;

namespace AiChromeProxy.Domain.Packages;

/// <summary>
/// <c>packages.nuget.chunk</c>: part of a file of the upload, files one after another (a file of size 0 is one empty chunk). A file's first chunk
/// (<paramref name="Offset"/> 0) carries its <paramref name="Size"/>, later chunks none (left out of the JSON); <paramref name="Path"/> is relative to
/// <c>&lt;id&gt;/&lt;version&gt;/</c> with forward slashes. <paramref name="Data"/> base64url without padding, ≤ <see cref="PackageLimits.ChunkSize"/> bytes raw.
/// Only the package's <paramref name="Last"/> chunk is answered: <c>packages.nuget.stored</c>, or the upload's first error.
/// </summary>
public sealed record NuGetChunkPayload(
	string UploadId,
	string Path,
	long Offset,
	string Data,
	bool Last,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null);
