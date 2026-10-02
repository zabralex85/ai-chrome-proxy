using AiChromeProxy.Shared;

namespace AiChromeProxy.Server.Transport;

public interface IEnvelopeHandler
{
	string Type { get; }

	Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct);
}
