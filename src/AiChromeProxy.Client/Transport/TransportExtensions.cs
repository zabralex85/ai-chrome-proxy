using System.Text.Json;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Client.Transport;

public static class TransportExtensions
{
	/// <summary>Sends <paramref name="request"/> under a fresh correlation id and waits for the reply that carries it.</summary>
	/// <exception cref="RequestFailedException">The server answered with <c>error</c>.</exception>
	/// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>.</exception>
	public static async Task<Envelope> RequestAsync(this ITransport transport, Envelope request, TimeSpan timeout, CancellationToken ct = default)
	{
		var id = Guid.NewGuid().ToString("N");
		var reply = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnReceived(Envelope e)
		{
			if (e.CorrelationId == id)
			{
				reply.TrySetResult(e);
			}
		}

		transport.Received += OnReceived;
		try
		{
			await transport.SendAsync(request with { CorrelationId = id }, ct);
			var envelope = await reply.Task.WaitAsync(timeout, ct);
			if (envelope.Type == MessageTypes.Error)
			{
				var error = envelope.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web);
				throw new RequestFailedException(error?.Code ?? ErrorCodes.Internal, error?.Message);
			}

			return envelope;
		}
		finally
		{
			transport.Received -= OnReceived;
		}
	}
}
