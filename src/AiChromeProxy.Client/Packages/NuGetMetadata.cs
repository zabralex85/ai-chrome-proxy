using System.Text.Json;
using AiChromeProxy.Domain.Packages;

namespace AiChromeProxy.Client.Packages;

/// <summary>Reads a package's <c>.nupkg.metadata</c> in the NuGet cache.</summary>
public static class NuGetMetadata
{
	/// <summary><c>contentHash</c> (a valid base64 sha512) and <c>source</c> of a <c>.nupkg.metadata</c>; null when it cannot be read as one.</summary>
	public static (string ContentHash, string? Source)? Parse(string json)
	{
		try
		{
			using (var document = JsonDocument.Parse(json))
			{
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object
					|| !root.TryGetProperty("contentHash", out var hash) || hash.ValueKind != JsonValueKind.String
					|| hash.GetString() is not { } contentHash || !NuGetNames.IsValidContentHash(contentHash))
				{
					return null;
				}

				var source = root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
				return (contentHash, source);
			}
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
