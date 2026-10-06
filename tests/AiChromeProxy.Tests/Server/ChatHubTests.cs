using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Tests.Application;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace AiChromeProxy.Tests.Server;

/// <summary>The real Server over SignalR with a scripted agent: a chat run streams to the repo's subscribers.</summary>
public sealed class ChatHubTests : IAsyncDisposable
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	private readonly TestAccessIssuer _issuer = new();
	private readonly FakeAgentRunner _agent = new();
	private readonly string _testRoot = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _mirror;
	private readonly WebApplicationFactory<Program> _factory;

	public ChatHubTests()
	{
		_mirror = Path.Combine(_testRoot, "mirror");
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.UseSetting("Mirror:Root", _mirror);
			b.UseSetting("Projects:Database", Path.Combine(_testRoot, "aicp.db"));
			b.UseSetting("Agent:Command", Path.Combine(AppContext.BaseDirectory, "AiChromeProxy.FakeAgent.exe"));
			b.UseSetting("Agent:Env:FAKE_AGENT_MCP_LIST", Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "claude-tools", "mcp-list-2.1.289.txt"));
			b.UseSetting("Agent:Env:FAKE_AGENT_PLUGIN_LIST", Path.Combine(_testRoot, "plugins.json"));
			b.ConfigureServices(s =>
			{
				s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler());
				s.AddSingleton<IAgentRunner>(_agent);
			});
		});
	}

	[Fact]
	public async Task Send_EventsReachSubscribers_RunSurvivesTheSendersDisconnect()
	{
		var ct = TestContext.Current.CancellationToken;
		Directory.CreateDirectory(Path.Combine(_mirror, "r"));
		var result = new TaskCompletionSource<ChatEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		var prompt = new TaskCompletionSource<ChatEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		await using (var watcher = new SignalRTransport(Connection()))
		{
			watcher.Received += e =>
			{
				if (e.Type == MessageTypes.ChatEvent && Read<ChatEvent>(e) is { } chat)
				{
					(chat.Kind == ChatEventKinds.Result ? result : chat.Kind == ChatEventKinds.Prompt ? prompt : null)?.TrySetResult(chat);
				}
			};
			await watcher.ConnectAsync(ct);
			await watcher.RequestAsync(Envelope.Create(MessageTypes.ChatOpen, new ChatOpenPayload("r")), Timeout, ct);

			ChatStartedPayload started;
			FakeAgentProcess process;
			await using (var sender = new SignalRTransport(Connection()))
			{
				await sender.ConnectAsync(ct);
				started = Read<ChatStartedPayload>(await sender.RequestAsync(Envelope.Create(MessageTypes.ChatSend, new ChatSendPayload("r", null, "Hi")), Timeout, ct));
				process = await _agent.NextAsync();
			}

			process.Write("""{"type":"result","subtype":"success","is_error":false,"result":"Hi."}""");
			process.Exit();

			var sent = await prompt.Task.WaitAsync(Timeout, ct);
			Assert.Equal((started.SessionId, 1L, "Hi"), (sent.SessionId, sent.Seq, sent.Text));
			var done = await result.Task.WaitAsync(Timeout, ct);
			Assert.Equal((started.SessionId, started.RunId, true), (done.SessionId, done.RunId, done.Ok));
			Assert.Equal(Path.Combine(_mirror, "r"), process.Run.RepoFolder);
			Assert.False(process.Killed);
		}
	}

	[Fact]
	public async Task ToolsCheck_RunsTheProbeInTheMirror_ThenGetShowsTheStoredSnapshot()
	{
		var ct = TestContext.Current.CancellationToken;
		Directory.CreateDirectory(Path.Combine(_mirror, "r"));
		await File.WriteAllTextAsync(Path.Combine(_testRoot, "plugins.json"), """[{"id":"design@market","version":"1.0.0","enabled":true}]""", ct);
		await using (var client = new SignalRTransport(Connection()))
		{
			await client.ConnectAsync(ct);
			await client.RequestAsync(Envelope.Create(MessageTypes.ProjectSettingsSet, new ProjectSettingsPayload("r", new ProjectSettings { AgentDisabledPlugins = ["design@market"] })), Timeout, ct);

			var checkedTools = Read<ClaudeToolsPayload>(await client.RequestAsync(Envelope.Create(MessageTypes.AgentToolsCheck, new ClaudeToolsRequest("r")), TimeSpan.FromSeconds(60), ct));
			var stored = Read<ClaudeToolsPayload>(await client.RequestAsync(Envelope.Create(MessageTypes.AgentToolsGet, new ClaudeToolsRequest("r")), Timeout, ct));

			Assert.Null(checkedTools.Error);
			Assert.Equal((ClaudeToolsSnapshot.FromCheck, 10), (checkedTools.From, checkedTools.Servers.Count));
			Assert.Equal(new ClaudePluginRow("design@market", "design", "1.0.0", true, false), Assert.Single(checkedTools.Plugins));
			Assert.Equal(checkedTools.CheckedAt, stored.CheckedAt);
			Assert.Equal(checkedTools.Servers, stored.Servers);
			Assert.Equal(checkedTools.Plugins, stored.Plugins);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _factory.DisposeAsync();
		await TestFolder.DeleteAsync(_testRoot);
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private HubConnection Connection()
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
					ws.ConfigureRequest = r =>
					{
						r.Headers[HeaderNames.Origin] = $"https://{ServerHostingTests.PublicHost}";
						r.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token();
					};
					return await ws.ConnectAsync(ctx.Uri, ct);
				};
			})
			.Build();
	}
}
