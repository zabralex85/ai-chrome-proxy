namespace AiChromeProxy.Domain.Packages;

/// <summary>Checks on NuGet ids, versions and sources from the protocol.</summary>
public static class NuGetNames
{
	public const int MaxIdLength = 100;

	public const int MaxVersionLength = 64;

	/// <summary><c>[A-Za-z0-9._-]</c>, at most <see cref="MaxIdLength"/>, starting with a letter, digit or <c>_</c>, without <c>..</c>.</summary>
	public static bool IsValidId(string? id) =>
		id is { Length: > 0 and <= MaxIdLength }
		&& (char.IsAsciiLetterOrDigit(id[0]) || id[0] == '_')
		&& !id.Contains("..", StringComparison.Ordinal)
		&& id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

	/// <summary><c>[A-Za-z0-9.+-]</c>, at most <see cref="MaxVersionLength"/>, starting with a letter or digit, without <c>..</c>.</summary>
	public static bool IsValidVersion(string? version) =>
		version is { Length: > 0 and <= MaxVersionLength }
		&& char.IsAsciiLetterOrDigit(version[0])
		&& !version.Contains("..", StringComparison.Ordinal)
		&& version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');

	/// <summary>Base64 of exactly 64 bytes (sha512), as in <c>.nupkg.metadata</c>.</summary>
	public static bool IsValidContentHash(string? hash)
	{
		if (hash is not { Length: 88 })
		{
			return false;
		}

		var bytes = new byte[66];
		return Convert.TryFromBase64String(hash, bytes, out var written) && written == 64;
	}

	/// <summary>A source on nuget.org (<c>api.nuget.org</c>): the server downloads such packages itself.</summary>
	public static bool IsPublicSource(string? source) =>
		Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, "api.nuget.org", StringComparison.OrdinalIgnoreCase);
}
