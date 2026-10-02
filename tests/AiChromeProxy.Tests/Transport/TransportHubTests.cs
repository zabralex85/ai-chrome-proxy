using System.Net;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Shared;
using AiChromeProxy.Tests.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Transport;

/// <summary>End-to-end: real Server pipeline (Access check on) + real SignalRTransport over WebSocket.</summary>
public sealed class TransportHubTests : IAsyncDisposable
{
	private readonly TestAccessIssuer _issuer = new();
	private readonly WebApplicationFactory<Program> _factory;

	public TransportHubTests()
	{
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseStaticWebAssets();
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler()));
		});
	}

	[Fact]
	public async Task Ping_OverWebSocket_ReturnsPong()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var transport = new SignalRTransport(Connection(_issuer.Token()));
		var states = new List<TransportState>();
		transport.StateChanged += states.Add;
		var pong = new TaskCompletionSource<Envelope>();
		transport.Received += e => pong.TrySetResult(e);

		await transport.ConnectAsync(ct);
		await transport.SendAsync(Envelope.Create(MessageTypes.Ping, new { }, "rt-1"), ct);
		var reply = await pong.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

		Assert.Equal(TransportState.Connected, transport.State);
		Assert.Equal([TransportState.Connecting, TransportState.Connected], states);
		Assert.Equal(MessageTypes.Pong, reply.Type);
		Assert.Equal("rt-1", reply.CorrelationId);
	}

	[Fact]
	public async Task NoToken_ConnectionRejected()
	{
		await using var transport = new SignalRTransport(Connection(token: null));

		var ex = await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));
		Assert.Contains("401", ex.Message);
		Assert.Equal(TransportState.Disconnected, transport.State);
	}

	[Theory]
	[InlineData("GET", "/")]
	[InlineData("GET", "/index.html")]
	[InlineData("GET", "/_framework/blazor.webassembly.js")]
	[InlineData("GET", "/css/app.css")]
	[InlineData("GET", "/some/client/route")]
	[InlineData("POST", "/hub/negotiate?negotiateVersion=1")]
	public async Task NoToken_EveryEntryPoint_401(string method, string path)
	{
		using var client = _factory.CreateClient();
		using var request = new HttpRequestMessage(new HttpMethod(method), path);

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task StaticFile_WithToken_200()
	{
		using var client = _factory.CreateClient();
		using var request = new HttpRequestMessage(HttpMethod.Get, "/css/app.css");
		request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
	}

	[Fact]
	public void Production_WithoutAccessConfig_FailsToStart()
	{
		// Explicit empty values: the machine running the tests may have real CloudflareAccess__* env vars set.
		using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("CloudflareAccess:TeamDomain", string.Empty);
			b.UseSetting("CloudflareAccess:Audience", string.Empty);
		});

		var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("CloudflareAccess", ex.ToString());
	}

	public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

	private HubConnection Connection(string? token)
	{
		var server = _factory.Server;
		return new HubConnectionBuilder()
			.WithUrl(new Uri(server.BaseAddress, TransportHub.Path), o =>
			{
				o.Transports = HttpTransportType.WebSockets;
				o.SkipNegotiation = true;
				o.WebSocketFactory = async (ctx, ct) =>
				{
					var ws = server.CreateWebSocketClient();
					if (token is not null)
					{
						ws.ConfigureRequest = r => r.Headers[CloudflareAccessMiddleware.HeaderName] = token;
					}

					return await ws.ConnectAsync(ctx.Uri, ct);
				};
			})
			.Build();
	}
}
