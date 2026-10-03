using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using Microsoft.AspNetCore.SignalR;

namespace AiChromeProxy.Server.Transport;

public sealed class TransportHub(EnvelopeRouter router, SyncSessions syncSessions, IHubContext<TransportHub> hub, TimeProvider time, ILogger<TransportHub> logger) : Hub
{
	public const string Path = "/hub";
	public const string ReceiveMethod = "Receive";
	private const uint MaxTimerMilliseconds = uint.MaxValue - 1;
	private const string ExpiryTimerKey = "aicp.expiry-timer";

	public async Task Send(Envelope? envelope)
	{
		var request = envelope ?? new Envelope(string.Empty, default);
		var connectionId = Context.ConnectionId;
		var context = new EnvelopeContext(
			connectionId,
			Context.GetHttpContext()?.Items[CloudflareAccessMiddleware.EmailItem] as string,
			(e, ct) => hub.Clients.Client(connectionId).SendAsync(ReceiveMethod, e, ct));

		Envelope? reply;
		try
		{
			reply = await router.RouteAsync(request, context, Context.ConnectionAborted);
		}
		catch (Exception ex) when (!Context.ConnectionAborted.IsCancellationRequested)
		{
			// Details stay in the log; the client only learns that it failed.
			logger.LogError(ex, "Handler for {Type} failed", request.Type);
			reply = EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}

		if (reply is not null)
		{
			await Clients.Caller.SendAsync(ReceiveMethod, reply, Context.ConnectionAborted);
		}
	}

	public override Task OnConnectedAsync()
	{
		// A connection outlives the request that carried the token: drop it when the token expires (the client reconnects through Access).
		if (Context.GetHttpContext()?.Items[CloudflareAccessMiddleware.ExpiryItem] is DateTimeOffset expiry)
		{
			var context = Context;
			// exp plus the validator's skew: a token accepted inside the skew window must not be aborted in a reconnect loop.
			var left = expiry + CloudflareAccessTokenValidator.ClockSkew - time.GetUtcNow();
			context.Items[ExpiryTimerKey] = time.CreateTimer(_ => context.Abort(), null, left > TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Min(left.TotalMilliseconds, MaxTimerMilliseconds)) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
		}

		return base.OnConnectedAsync();
	}

	public override Task OnDisconnectedAsync(Exception? exception)
	{
		(Context.Items[ExpiryTimerKey] as IDisposable)?.Dispose();
		syncSessions.Close(Context.ConnectionId);
		return base.OnDisconnectedAsync(exception);
	}
}
