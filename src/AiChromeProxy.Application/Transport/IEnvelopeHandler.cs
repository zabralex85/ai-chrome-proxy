using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public interface IEnvelopeHandler
{
	string Type { get; }

	/// <returns>The reply (sent with the request's correlation id by the caller), or null for none.</returns>
	/// <exception cref="EnvelopeException">The request is answered with <c>error {code, message}</c>.</exception>
	Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct);
}
