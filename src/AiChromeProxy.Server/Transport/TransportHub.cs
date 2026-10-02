using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using Microsoft.AspNetCore.SignalR;

namespace AiChromeProxy.Server.Transport;

public sealed class TransportHub(EnvelopeRouter router) : Hub
{
	public const string Path = "/hub";
	public const string ReceiveMethod = "Receive";

	public async Task Send(Envelope envelope)
	{
		var reply = await router.RouteAsync(envelope, Context.ConnectionAborted);
		if (reply is not null)
		{
			await Clients.Caller.SendAsync(ReceiveMethod, reply, Context.ConnectionAborted);
		}
	}
}
