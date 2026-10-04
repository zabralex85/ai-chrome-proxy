using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Chat;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Tests.Application;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiChromeProxy.Tests.Server;

/// <summary>The approval MCP endpoint of the real Server: loopback and a running run's token only; a tool call waits for <c>chat.approve</c>.</summary>
public sealed class ApprovalMcpTests : IAsyncDisposable
{
	private const string Repo = "r";

	/// <summary>Test header naming the caller's IP (TestServer has no real connection).</summary>
	private const string IpHeader = "X-Test-Remote-Ip";

	private static readonly string FakeAgent = Path.Combine(AppContext.BaseDirectory, "AiChromeProxy.FakeAgent.exe");

	/// <summary>Requests addressed like the approval URL (<c>Host: 127.0.0.1</c>).</summary>
	private static readonly WebApplicationFactoryClientOptions Local = new() { BaseAddress = new Uri("http://127.0.0.1") };

	private readonly TestAccessIssuer _issuer = new();
	private readonly FakeAgentRunner _agent = new();
	private readonly string _testRoot = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _mirror;
	private readonly List<Envelope> _pushed = [];
	private WebApplicationFactory<Program> _factory;
	private ChatService? _chat;

	public ApprovalMcpTests()
	{
		_mirror = Path.Combine(_testRoot, "mirror");
		Directory.CreateDirectory(Path.Combine(_mirror, Repo));
		_factory = Factory(b => b.ConfigureServices(s =>
		{
			s.AddSingleton<IAgentRunner>(_agent);
			s.AddSingleton<IStartupFilter>(new RemoteIpFilter());
		}));
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private ChatService Chat => _chat ??= _factory.Services.GetRequiredService<ChatService>();

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		await TestFolder.DeleteAsync(_testRoot);
	}

	[Fact]
	public async Task ApprovalUrl_IsTheLoopbackEndpointOnTheServerPort()
	{
		var (_, process) = await StartRunAsync();

		Assert.Equal("http://127.0.0.1:5180/mcp/approve", process.Run.ApprovalUrl);
		Assert.False(string.IsNullOrEmpty(process.Run.ApprovalToken));
	}

	[Fact]
	public async Task RemoteCaller_404_EvenWithAccessAndTheRunsToken()
	{
		var (_, process) = await StartRunAsync();

		using (var request = Post(process.Run.ApprovalToken))
		{
			request.Headers.Add(IpHeader, "203.0.113.7");
			request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());

			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}
	}

	[Fact]
	public async Task LoopbackThroughTheTunnel_NotLocal_AccessRequired()
	{
		var (_, process) = await StartRunAsync();

		using (var request = Post(process.Run.ApprovalToken))
		{
			request.Headers.Add("Cf-Connecting-IP", "203.0.113.7");

			Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(request));
		}

		using (var request = Post(process.Run.ApprovalToken))
		{
			request.Headers.Add("Cf-Connecting-IP", "203.0.113.7");
			request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());

			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}
	}

	[Fact]
	public async Task LoopbackWithThePublicHost_NotLocal_AccessRequired()
	{
		var (_, process) = await StartRunAsync();

		foreach (var host in new[] { ServerHostingTests.PublicHost, "localhost" })
		{
			using (var request = Post(process.Run.ApprovalToken))
			{
				request.Headers.Host = host;

				Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(request));
			}

			using (var request = Post(process.Run.ApprovalToken))
			{
				request.Headers.Host = host;
				request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());

				Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
			}
		}
	}

	[Fact]
	public async Task Loopback_WrongOrMissingToken_404()
	{
		var (_, process) = await StartRunAsync();

		using (var request = Post("wrong"))
		{
			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}

		using (var request = Post(process.Run.ApprovalToken + "x"))
		{
			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}

		using (var request = Post(null))
		{
			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}

		using (var request = Post(null))
		{
			request.Headers.TryAddWithoutValidation("Authorization", process.Run.ApprovalToken);
			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}
	}

	[Fact]
	public async Task Loopback_OtherPaths_StillNeedAccess()
	{
		var (_, process) = await StartRunAsync();

		foreach (var path in new[] { "/", "/hub/negotiate", "/mcp/approve/sse", "/mcp" })
		{
			using (var request = new HttpRequestMessage(HttpMethod.Post, path))
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", process.Run.ApprovalToken);
				Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(request));
			}
		}
	}

	[Fact]
	public async Task RunEnded_Token404()
	{
		var (started, process) = await StartRunAsync();
		Chat.Cancel(started.RunId);
		await WaitAsync(e => e.Kind == ChatEventKinds.Result);

		using (var request = Post(process.Run.ApprovalToken))
		{
			Assert.Equal(HttpStatusCode.NotFound, await SendAsync(request));
		}
	}

	[Fact]
	public async Task McpClient_Allow_UpdatedInputIsTheInput()
	{
		var (started, process) = await StartRunAsync();
		await using (var client = await ClientAsync(process.Run.ApprovalToken!))
		{
			var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: Ct));
			Assert.Equal("approve", tool.Name);
			Assert.Equal(["input", "tool_name"], tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());

			var call = CallAsync(client, "Bash", new JsonObject { ["command"] = "ls -la" });
			var permission = await WaitAsync(e => e.Kind == ChatEventKinds.Permission);
			Assert.Equal((started.RunId, "Bash", "ls -la"), (permission.RunId, permission.Name, permission.Summary));
			Chat.Approve(new ChatApprovePayload(started.RunId, permission.RequestId!, ChatDecisions.Allow));

			Assert.Equal("""{"behavior":"allow","updatedInput":{"command":"ls -la"}}""", await call);
		}
	}

	[Fact]
	public async Task McpClient_Deny_DeniedInTheChat()
	{
		var (started, process) = await StartRunAsync();
		await using (var client = await ClientAsync(process.Run.ApprovalToken!))
		{
			var call = CallAsync(client, "WebFetch", new JsonObject { ["url"] = "https://example.com" });
			var permission = await WaitAsync(e => e.Kind == ChatEventKinds.Permission);
			Chat.Approve(new ChatApprovePayload(started.RunId, permission.RequestId!, ChatDecisions.Deny));

			Assert.Equal("""{"behavior":"deny","message":"Denied in the chat."}""", await call);
		}
	}

	[Fact]
	public async Task McpClient_ServerStopping_Denied()
	{
		var (_, process) = await StartRunAsync();
		await using (var client = await ClientAsync(process.Run.ApprovalToken!))
		{
			var call = CallAsync(client, "Bash", new JsonObject { ["command"] = "ls" });
			await WaitAsync(e => e.Kind == ChatEventKinds.Permission);

			_factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

			Assert.Equal("""{"behavior":"deny","message":"Denied in the chat."}""", await call);
			Assert.Equal(ChatDecisions.Deny, (await WaitAsync(e => e.Kind == ChatEventKinds.PermissionResolved)).Decision);
		}
	}

	[Fact]
	public async Task FakeAgent_ApprovesThroughTheRealEndpoint_RunEndsNormally()
	{
		await StopAsync();
		var port = FreePort();
		var script = Path.Combine(_testRoot, "approve.jsonl");
		await File.WriteAllLinesAsync(
			script,
			[
				"""{"type":"system","subtype":"init","session_id":"s1"}""",
				"""#approve Bash {"command":"ls"}""",
				"""{"type":"result","subtype":"success","is_error":false,"result":"Done."}""",
			],
			Ct);
		_factory = Factory(b =>
		{
			b.UseSetting("Server:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
			b.UseSetting("Agent:Command", FakeAgent);
			b.UseSetting("Agent:Env:FAKE_AGENT_SCRIPT", script);
		});
		_factory.UseKestrel();
		_factory.StartServer();

		var started = Chat.Send(new ChatSendPayload(Repo, null, "List"), Context());
		var permission = await WaitAsync(e => e.Kind == ChatEventKinds.Permission);
		Assert.Equal(("Bash", "ls"), (permission.Name, permission.Summary));
		Chat.Approve(new ChatApprovePayload(started.RunId, permission.RequestId!, ChatDecisions.Allow));

		var result = await WaitAsync(e => e.Kind == ChatEventKinds.Result);
		Assert.True(result.Ok, result.Error);
		Assert.Contains(Events(), e => e.Kind == ChatEventKinds.Message && e.Text == "approve: allow");
	}

	private static async Task<string> CallAsync(McpClient client, string tool, JsonObject input)
	{
		var result = await client.CallToolAsync("approve", new Dictionary<string, object?> { ["tool_name"] = tool, ["input"] = input }, cancellationToken: Ct);
		return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
	}

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private static HttpRequestMessage Post(string? token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/approve")
		{
			Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
		};
		request.Headers.Accept.ParseAdd("application/json");
		request.Headers.Accept.ParseAdd("text/event-stream");
		if (token is not null)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		return request;
	}

	/// <summary>
	/// Ends the runs, then the host. The factory's DisposeAsync returns before the app's own thread has disposed the services, so runs
	/// left going would still be writing their results to aicp.db while the test deletes it.
	/// </summary>
	private async Task StopAsync()
	{
		if (_chat is not null)
		{
			await _chat.DisposeAsync();
			_chat = null;
		}

		await _factory.DisposeAsync();
	}

	private WebApplicationFactory<Program> Factory(Action<IWebHostBuilder> configure) =>
		new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.UseSetting("Mirror:Root", _mirror);
			b.UseSetting("Projects:Database", Path.Combine(_testRoot, "aicp.db"));
			b.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler()));
			configure(b);
		});

	private async Task<HttpStatusCode> SendAsync(HttpRequestMessage request)
	{
		using (var client = _factory.CreateClient(Local))
		{
			using (var response = await client.SendAsync(request, Ct))
			{
				return response.StatusCode;
			}
		}
	}

	private async Task<McpClient> ClientAsync(string token)
	{
		var http = _factory.CreateClient(Local);
		var transport = new HttpClientTransport(
			new HttpClientTransportOptions
			{
				Endpoint = new Uri(http.BaseAddress!, "/mcp/approve"),
				TransportMode = HttpTransportMode.StreamableHttp,
				AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
			},
			http,
			ownsHttpClient: true);
		return await McpClient.CreateAsync(transport, cancellationToken: Ct);
	}

	private async Task<(ChatStartedPayload Started, FakeAgentProcess Process)> StartRunAsync()
	{
		var started = Chat.Send(new ChatSendPayload(Repo, null, "Hi"), Context());
		return (started, await _agent.NextAsync());
	}

	private EnvelopeContext Context() => new(
		"c1",
		null,
		(e, _) =>
		{
			lock (_pushed)
			{
				_pushed.Add(e);
			}

			return Task.CompletedTask;
		});

	private List<ChatEvent> Events()
	{
		lock (_pushed)
		{
			return [.. _pushed.Where(e => e.Type == MessageTypes.ChatEvent).Select(e => e.Payload.Deserialize<ChatEvent>(JsonSerializerOptions.Web)!)];
		}
	}

	private async Task<ChatEvent> WaitAsync(Func<ChatEvent, bool> match)
	{
		for (var i = 0; i < 1500 && !Events().Any(match); i++)
		{
			await Task.Delay(10, Ct);
		}

		return Events().First(match);
	}

	/// <summary>Sets the caller's IP: loopback unless the test header names another.</summary>
	private sealed class RemoteIpFilter : IStartupFilter
	{
		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
		{
			app.Use(async (context, nextMiddleware) =>
			{
				string? ip = context.Request.Headers[IpHeader];
				context.Connection.RemoteIpAddress = IPAddress.Parse(ip ?? "127.0.0.1");
				await nextMiddleware(context);
			});
			next(app);
		};
	}
}
