using AiChromeProxy.Shared;

namespace AiChromeProxy.Server.Transport;

public sealed class PingHandler(TimeProvider time) : IEnvelopeHandler
{
	public string Type => MessageTypes.Ping;

	public Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct)
	{
		var pong = Envelope.Create(MessageTypes.Pong, new { serverTime = time.GetUtcNow().ToString("O") }, request.CorrelationId);
		return Task.FromResult<Envelope?>(pong);
	}
}
