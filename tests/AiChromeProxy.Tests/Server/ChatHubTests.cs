using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
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

	public async ValueTask DisposeAsync()
	{
		await _factory.DisposeAsync();
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		if (Directory.Exists(_testRoot))
		{
			Directory.Delete(_testRoot, recursive: true);
		}
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
