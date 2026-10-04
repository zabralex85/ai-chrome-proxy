using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Transport;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Server.Security;

/// <summary>Refuses cross-site requests to the hub: a request that names an Origin is accepted only for the tunnel's own origin (plus loopback in Development); one without an Origin (native clients, same-origin long polling) passes.</summary>
public sealed class HubOriginFilter(RequestDelegate next, IOptions<ServerOptions> options, IHostEnvironment environment)
{
	public async Task InvokeAsync(HttpContext context)
	{
		if (context.Request.Path.StartsWithSegments(TransportHub.Path) && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin && !IsAllowed(origin))
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
