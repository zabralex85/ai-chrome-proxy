using System.Text.RegularExpressions;

namespace AiChromeProxy.Infrastructure.Hosted;

/// <summary>An invite link <c>https://&lt;service host&gt;/invite/&lt;code&gt;</c>: the service origin and the invite code.</summary>
public sealed partial record InviteLink(Uri Origin, string Code)
{
	/// <summary>Parses an invite link; false unless it follows the protocol exactly (https, or http for localhost; no query, fragment or user info).</summary>
	public static bool TryParse(string? text, out InviteLink? link)
	{
		link = null;
		if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
			|| uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
		{
			return false;
		}

		var local = uri.Host is "localhost" or "127.0.0.1";
		if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && local))
		{
			return false;
		}

		const string prefix = "/invite/";
		var path = uri.AbsolutePath;
		if (!path.StartsWith(prefix, StringComparison.Ordinal))
		{
			return false;
		}

		var code = path[prefix.Length..].TrimEnd('/');
		if (path.EndsWith("//", StringComparison.Ordinal) || !CodePattern().IsMatch(code))
		{
			return false;
		}

		link = new InviteLink(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), code);
		return true;
	}

	[GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
	private static partial Regex CodePattern();
}
