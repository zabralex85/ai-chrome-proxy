using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Server;

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

			// Cache headers as published: with the build manifest MapStaticAssets otherwise sends no-cache for everything.
			b.UseSetting("ReloadStaticAssetsAtRuntime", "false");
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.ConfigureServices(s =>
			{
				s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler());
				s.AddSingleton<IEnvelopeHandler, ProbeHandler>();
			});
		});
	}

	[Fact]
	public async Task Ping_OverWebSocket_ReturnsPong()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
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
	}

	[Fact]
	public async Task Request_Ping_ReturnsPongWithCorrelationId()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			await transport.ConnectAsync(ct);

			var reply = await transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }), TimeSpan.FromSeconds(10), ct);

			Assert.Equal(MessageTypes.Pong, reply.Type);
		}
	}

	[Theory]
	[InlineData("fail", ErrorCodes.NotFound, "missing")]
	[InlineData("boom", ErrorCodes.Internal, ErrorCodes.Internal)]
	public async Task Request_HandlerFails_ErrorReplyWithCode_NoInternalDetails(string mode, string code, string message)
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			await transport.ConnectAsync(ct);

			var ex = await Assert.ThrowsAsync<RequestFailedException>(
				() => transport.RequestAsync(Envelope.Create(ProbeHandler.MessageType, new { mode }), TimeSpan.FromSeconds(10), ct));

			Assert.Equal(code, ex.Code);
			Assert.Equal(message, ex.Message);
		}
	}

	[Fact]
	public async Task Request_NullType_BadRequest()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			await transport.ConnectAsync(ct);

			var ex = await Assert.ThrowsAsync<RequestFailedException>(
				() => transport.RequestAsync(new Envelope(null!, JsonSerializer.SerializeToElement(new { })), TimeSpan.FromSeconds(10), ct));

			Assert.Equal(ErrorCodes.BadRequest, ex.Code);
		}
	}

	[Fact]
	public async Task Handler_PushesThroughContext_WithAccessEmailAndConnectionId()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			var pushed = new TaskCompletionSource<Envelope>();
			transport.Received += e => pushed.TrySetResult(e);
			await transport.ConnectAsync(ct);

			await transport.SendAsync(Envelope.Create(ProbeHandler.MessageType, new { mode = "push" }), ct);
			var push = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

			Assert.Equal("test.pushed", push.Type);
			Assert.Equal("user@example.com", push.Payload.GetProperty("email").GetString());
			Assert.False(string.IsNullOrEmpty(push.Payload.GetProperty("connectionId").GetString()));
		}
	}

	[Fact]
	public async Task NoToken_ConnectionRejected()
	{
		await using (var transport = new SignalRTransport(Connection(token: null)))
		{
			var ex = await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));
			Assert.Contains("401", ex.Message);
			Assert.Equal(TransportState.Disconnected, transport.State);
		}
	}

	[Theory]
	[InlineData("GET", "/")]
	[InlineData("GET", "/index.html")]
	[InlineData("GET", "/_framework/blazor.webassembly.js")]
	[InlineData("GET", "/css/app.css")]
	[InlineData("GET", "/js/fsaccess.js")]
	[InlineData("GET", "/some/client/route")]
	[InlineData("POST", "/hub/negotiate?negotiateVersion=1")]
	public async Task NoToken_EveryEntryPoint_401(string method, string path)
	{
		using (var client = _factory.CreateClient())
		{
			using (var request = new HttpRequestMessage(new HttpMethod(method), path))
			{
				using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
				{
					Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
				}
			}
		}
	}

	/// <summary>
	/// The page is never cached (a proxy may stretch <c>no-cache</c> into hours) and names only fingerprinted, immutable files: the Blazor script,
	/// the stylesheet and, through the import map, <c>dotnet.js</c> (the boot manifest) and the fsaccess module.
	/// </summary>
	[Theory]
	[InlineData("/")]
	[InlineData("/index.html")]
	[InlineData("/some/client/route")]
	public async Task Page_WithToken_NoStore_LoadsOnlyFingerprintedAssets(string path)
	{
		var ct = TestContext.Current.CancellationToken;
		using (var client = _factory.CreateClient())
		{
			using (var response = await Send(client, path, ct))
			{
				var html = await response.Content.ReadAsStringAsync(ct);

				Assert.Equal(HttpStatusCode.OK, response.StatusCode);
				Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
				Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
				Assert.DoesNotContain("#[", html, StringComparison.Ordinal);
				Assert.DoesNotContain("{{", html, StringComparison.Ordinal);

				var script = Assert.Single(Regex.Matches(html, "<script src=\"([^\"]+)\"")).Groups[1].Value;
				var stylesheet = Assert.Single(Regex.Matches(html, "<link rel=\"stylesheet\" href=\"([^\"]+)\"")).Groups[1].Value;
				var importMap = Regex.Match(html, "<script type=\"importmap\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
				var imports = JsonDocument.Parse(importMap).RootElement.GetProperty("imports");
				var dotnet = imports.GetProperty("./_framework/dotnet.js").GetString()!;
				var fsaccess = imports.GetProperty("./js/fsaccess.js").GetString()!;

				Assert.Matches(@"^_framework/blazor\.webassembly\.[a-z0-9]+\.js$", script);
				Assert.Matches(@"^css/app\.[a-z0-9]+\.css$", stylesheet);
				Assert.Matches(@"^\./_framework/dotnet\.[a-z0-9]+\.js$", dotnet);
				Assert.Matches(@"^\./js/fsaccess\.[a-z0-9]+\.js$", fsaccess);
				foreach (var asset in new[] { script, stylesheet, dotnet[2..], fsaccess[2..] })
				{
					using (var file = await Send(client, "/" + asset, ct))
					{
						Assert.Equal(HttpStatusCode.OK, file.StatusCode);
						Assert.Contains("immutable", file.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
					}
				}
			}
		}
	}

	[Fact]
	public void ClientIndexHtml_HasOnlyServerPlaceholders()
	{
		// Build placeholders are filled only when a Blazor app is published on its own; the hosting Server fills its own {{...}} ones.
		var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Client", "index.html"));

		Assert.DoesNotContain("#[", html, StringComparison.Ordinal);
		Assert.DoesNotContain("id=\"webassembly\"", html, StringComparison.Ordinal);
		Assert.Contains("<script type=\"importmap\">{{importmap}}</script>", html, StringComparison.Ordinal);
		Assert.Contains("<link rel=\"stylesheet\" href=\"{{stylesheet}}\" />", html, StringComparison.Ordinal);
		Assert.Contains("<script src=\"{{blazor-script}}\"></script>", html, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StaticFile_WithToken_200()
	{
		using (var client = _factory.CreateClient())
		{
			using (var request = new HttpRequestMessage(HttpMethod.Get, "/css/app.css"))
			{
				request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());

				using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
				{
					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
				}
			}
		}
	}

	[Fact]
	public async Task FsAccessScript_WithToken_ServedAsJavaScriptModule()
	{
		var ct = TestContext.Current.CancellationToken;
		using (var client = _factory.CreateClient())
		{
			using (var request = new HttpRequestMessage(HttpMethod.Get, "/js/fsaccess.js"))
			{
				request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());
				using (var response = await client.SendAsync(request, ct))
				{
					var script = await response.Content.ReadAsStringAsync(ct);

					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					Assert.Contains(response.Content.Headers.ContentType?.MediaType, new[] { "text/javascript", "application/javascript" });
					Assert.Contains("export async function pick()", script, StringComparison.Ordinal);
					Assert.Contains("showDirectoryPicker({ id: 'aicp', mode: 'read' })", script, StringComparison.Ordinal);
				}
			}
		}
	}

	[Fact]
	public void Production_WithoutAccessConfig_FailsToStart()
	{
		// Explicit empty values: the machine running the tests may have real CloudflareAccess__* env vars set.
		using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", string.Empty);
			b.UseSetting("CloudflareAccess:Audience", string.Empty);
		}))
		{
			var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

			Assert.Contains("CloudflareAccess", ex.ToString());
		}
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

	private async Task<HttpResponseMessage> Send(HttpClient client, string path, CancellationToken ct)
	{
		using (var request = new HttpRequestMessage(HttpMethod.Get, path))
		{
			request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());
			return await client.SendAsync(request, ct);
		}
	}

	/// <summary>Exercises the error contract and the context: <c>{mode: "fail" | "boom" | "push"}</c>.</summary>
	private sealed class ProbeHandler : IEnvelopeHandler
	{
		public const string MessageType = "test.probe";

		public string Type => MessageType;

		public async Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
		{
			switch (request.Payload.GetProperty("mode").GetString())
			{
				case "fail":
					throw new EnvelopeException(ErrorCodes.NotFound, "missing");
				case "push":
					await context.SendAsync(Envelope.Create("test.pushed", new { email = context.Email, connectionId = context.ConnectionId }), ct);
					return null;
				default:
					throw new InvalidOperationException("secret detail");
			}
		}
	}
}
