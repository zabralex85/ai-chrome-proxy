using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Infrastructure.Security;

public sealed class CloudflareAccessOptions
{
	public const string Section = "CloudflareAccess";

	public bool Enabled { get; set; } = true;

	public string TeamDomain { get; set; } = string.Empty;

	public string Audience { get; set; } = string.Empty;

	/// <returns>Null when valid; otherwise the message the Server fails with and the tray shows.</returns>
	public string? GetError(bool isDevelopment)
	{
		if (!Enabled)
		{
			return isDevelopment ? null : "CloudflareAccess:Enabled=false is allowed only in Development.";
		}

		if (string.IsNullOrWhiteSpace(TeamDomain) || string.IsNullOrWhiteSpace(Audience))
		{
			return "CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.";
		}

		return HostName.IsValid(TeamDomain)
			? null
			: $"CloudflareAccess:TeamDomain must be a bare host name like team.cloudflareaccess.com (no scheme, path, port or trailing slash); got '{TeamDomain}'.";
	}

	/// <summary>Fail closed: outside Development the check must be on and fully configured.</summary>
	public void Validate(IHostEnvironment env)
	{
		if (GetError(env.IsDevelopment()) is { } error)
		{
			throw new InvalidOperationException(error);
		}
	}
}
