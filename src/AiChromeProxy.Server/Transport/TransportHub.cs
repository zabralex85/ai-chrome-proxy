using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Server.Security;
using Microsoft.AspNetCore.SignalR;

namespace AiChromeProxy.Server.Transport;

public sealed class TransportHub(EnvelopeRouter router, SyncSessions syncSessions, IHubContext<TransportHub> hub, ILogger<TransportHub> logger) : Hub
{
	public const string Path = "/hub";
	public const string ReceiveMethod = "Receive";

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

	public override Task OnDisconnectedAsync(Exception? exception)
	{
		syncSessions.Close(Context.ConnectionId);
		return base.OnDisconnectedAsync(exception);
	}
}
