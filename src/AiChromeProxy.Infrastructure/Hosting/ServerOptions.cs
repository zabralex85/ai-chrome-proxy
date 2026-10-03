using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Section <c>Server</c>: where the Server listens and which public host name it answers to.</summary>
public sealed class ServerOptions
{
	public const string Section = "Server";
	public const int DefaultPort = 5180;

	public int Port { get; set; } = DefaultPort;

	/// <summary>Public host name of the tunnel (e.g. <c>code.example.com</c>); the only non-loopback <c>Host</c> header accepted.</summary>
	public string PublicHost { get; set; } = string.Empty;

	/// <summary>Host names for ASP.NET Core host filtering: the public host plus the loopback names (DNS-rebinding hardening).</summary>
	public IReadOnlyList<string> AllowedHosts() =>
		string.IsNullOrWhiteSpace(PublicHost) ? ["127.0.0.1", "localhost"] : [PublicHost, "127.0.0.1", "localhost"];

	/// <returns>Null when valid; otherwise the message the Server fails with and the tray shows.</returns>
	public string? GetError(bool isDevelopment)
	{
		if (string.IsNullOrWhiteSpace(PublicHost))
		{
			return isDevelopment ? null : "Server:PublicHost must be set outside Development (the tunnel's public host name, e.g. code.example.com).";
		}

		return HostName.IsValid(PublicHost)
			? null
			: $"Server:PublicHost must be a bare host name like code.example.com (no scheme, path, port or trailing slash); got '{PublicHost}'.";
	}

	/// <summary>Fail closed: outside Development the public host must be configured.</summary>
	public void Validate(IHostEnvironment env)
	{
		if (GetError(env.IsDevelopment()) is { } error)
		{
			throw new InvalidOperationException(error);
		}
	}
}
