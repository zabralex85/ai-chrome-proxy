using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public sealed class PingHandler(TimeProvider time) : IEnvelopeHandler
{
	private static readonly string ServerVersion = ProductVersion.Of(typeof(PingHandler).Assembly);

	public string Type => MessageTypes.Ping;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var pong = Envelope.Create(MessageTypes.Pong, new { serverTime = time.GetUtcNow().ToString("O"), serverVersion = ServerVersion }, request.CorrelationId);
		return Task.FromResult<Envelope?>(pong);
	}
}
