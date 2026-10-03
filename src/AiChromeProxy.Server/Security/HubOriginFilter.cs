using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AiChromeProxy.Server.Security;

/// <summary>Refuses cross-site WebSocket requests to the hub: only the tunnel's own origin (plus loopback in Development) is accepted.</summary>
public sealed class HubOriginFilter(RequestDelegate next, IOptions<ServerOptions> options, IHostEnvironment environment)
{
	public async Task InvokeAsync(HttpContext context)
	{
		if (context.Request.Path.StartsWithSegments(Transport.TransportHub.Path) && !IsAllowed(context.Request.Headers.Origin.ToString()))
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		await next(context);
	}

	private bool IsAllowed(string origin)
	{
		if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.PathAndQuery != "/")
		{
			return false;
		}

		var publicHost = options.Value.PublicHost;
		if (uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(publicHost) && uri.IsDefaultPort
			&& string.Equals(uri.Host, publicHost, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1");
	}
}
