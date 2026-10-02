using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public sealed class EnvelopeRouter
{
	private readonly Dictionary<string, IEnvelopeHandler> _handlers = new(StringComparer.Ordinal);

	public EnvelopeRouter(IEnumerable<IEnvelopeHandler> handlers)
	{
		foreach (var handler in handlers)
		{
			if (!_handlers.TryAdd(handler.Type, handler))
			{
				throw new InvalidOperationException($"Duplicate envelope handler for type '{handler.Type}'.");
			}
		}
	}

	public Task<Envelope?> RouteAsync(Envelope request, CancellationToken ct)
	{
		if (_handlers.TryGetValue(request.Type, out var handler))
		{
			return handler.HandleAsync(request, ct);
		}

		var error = Envelope.Create(MessageTypes.Error, new { code = "unknown_type", type = request.Type }, request.CorrelationId);
		return Task.FromResult<Envelope?>(error);
	}
}
