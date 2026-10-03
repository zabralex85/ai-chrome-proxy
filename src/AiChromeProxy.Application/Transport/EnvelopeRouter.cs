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

	/// <summary>The <c>error</c> reply to <paramref name="request"/>, carrying its correlation id.</summary>
	public static Envelope Error(Envelope request, ErrorPayload error) =>
		Envelope.Create(MessageTypes.Error, error, request.CorrelationId);

	/// <summary>Dispatches by <see cref="Envelope.Type"/>; expected failures become <c>error</c> replies, unexpected exceptions propagate to the hub.</summary>
	public async Task<Envelope?> RouteAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(request.Type))
		{
			return Error(request, new ErrorPayload(ErrorCodes.BadRequest, "Envelope type is required."));
		}

		if (!_handlers.TryGetValue(request.Type, out var handler))
		{
			return Error(request, new ErrorPayload(ErrorCodes.UnknownType, Type: request.Type));
		}

		try
		{
			return await handler.HandleAsync(request, context, ct);
		}
		catch (EnvelopeException ex)
		{
			return Error(request, new ErrorPayload(ex.Code, ex.Message));
		}
	}
}
