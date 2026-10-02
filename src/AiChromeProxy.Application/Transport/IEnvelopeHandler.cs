using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public interface IEnvelopeHandler
{
	string Type { get; }

	Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct);
}
