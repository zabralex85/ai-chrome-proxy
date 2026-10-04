using System.Net;

namespace AiChromeProxy.Infrastructure.Hosted;

/// <summary>A failed call to the hosted provisioning service; the message is safe to show to the user.</summary>
public sealed class HostedProvisioningException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
	/// <summary>HTTP status of the response; null when the service was not reached.</summary>
	public HttpStatusCode? StatusCode { get; } = statusCode;
}
