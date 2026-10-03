using System.Net;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>A failed Cloudflare API call, described by the first error of the response (never the API token).</summary>
public sealed class CloudflareApiException(string message, int code = 0, HttpStatusCode? statusCode = null) : Exception(message)
{
	/// <summary>Cloudflare's error code (<c>errors[0].code</c>); 0 when the response carried none.</summary>
	public int Code { get; } = code;

	/// <summary>HTTP status of the response; null when the failure was not an HTTP error status.</summary>
	public HttpStatusCode? StatusCode { get; } = statusCode;
}
