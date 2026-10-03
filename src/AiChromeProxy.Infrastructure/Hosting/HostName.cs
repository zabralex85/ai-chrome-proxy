namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Bare DNS host name check shared by the Server's startup validation and the tray's settings form.</summary>
public static class HostName
{
	/// <returns>True for <c>code.example.com</c>; false for a scheme, path, port, trailing dot/slash, IP address or blank.</returns>
	public static bool IsValid(string? value) =>
		!string.IsNullOrEmpty(value) && !value.EndsWith('.') && Uri.CheckHostName(value) == UriHostNameType.Dns;
}
