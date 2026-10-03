using System.Text.Json;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Client.Transport;

public static class TransportExtensions
{
	/// <summary>
	/// First connect, retried with the <see cref="ForeverRetryPolicy"/> delays until it succeeds or <paramref name="ct"/> is cancelled
	/// (SignalR's automatic reconnect only covers connections that were up once).
	/// </summary>
	/// <param name="delay">Waits between attempts; <c>Task.Delay</c> in the app, a recorder in tests.</param>
	public static async Task ConnectForeverAsync(this ITransport transport, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct = default)
	{
		for (var attempt = 0L; ; attempt++)
		{
			try
			{
				await transport.ConnectAsync(ct);
				return;
			}
			catch (Exception) when (!ct.IsCancellationRequested)
			{
				await delay(ForeverRetryPolicy.Delay(attempt), ct);
			}
		}
	}

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
