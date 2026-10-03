using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Server;

/// <summary>The real Server over SignalR: one small repo goes open → manifest → need → chunks → stored, and lands in a temp mirror.</summary>
public sealed class SyncHubTests : IAsyncDisposable
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	private readonly TestAccessIssuer _issuer = new();
	private readonly string _testRoot = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _mirror;
	private readonly string _database;
	private readonly WebApplicationFactory<Program> _factory;

	public SyncHubTests()
	{
		_mirror = Path.Combine(_testRoot, "mirror");
		_database = Path.Combine(_testRoot, "aicp.db");
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.UseSetting("Mirror:Root", _mirror);
			b.UseSetting("Projects:Database", _database);
			b.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler()));
		});
	}

	[Fact]
	public void HubKeepsSignalRDefaultMessageLimit()
	{
		var options = _factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;

		Assert.Equal(32 * 1024, options.MaximumReceiveMessageSize);
	}

	[Fact]
	public async Task SmallRepo_OpenManifestChunks_FilesInMirror_StaleFileDeleted()
	{
		var ct = TestContext.Current.CancellationToken;
		var repoRoot = Path.Combine(_mirror, "My_Repo");
		Directory.CreateDirectory(repoRoot);
		File.WriteAllText(Path.Combine(repoRoot, "stale.txt"), "old");
		File.WriteAllText(Path.Combine(repoRoot, "same.txt"), "same");
		var big = RandomNumberGenerator.GetBytes(SyncLimits.ChunkSize + 10);
		var files = new Dictionary<string, byte[]>
		{
			["same.txt"] = Encoding.UTF8.GetBytes("same"),
			["src/app.cs"] = Encoding.UTF8.GetBytes("class App { }"),
			["assets/big.bin"] = big,
		};

		await using (var transport = new SignalRTransport(Connection()))
		{
			await transport.ConnectAsync(ct);

			var opened = await transport.RequestAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("My Repo")), Timeout, ct);
			var repo = Read<SyncOpenPayload>(opened).Repo;
			var entries = files.Select(f => new ManifestEntry(f.Key, f.Value.Length, Sha(f.Value))).ToList();
			var need = await transport.RequestAsync(Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(repo, entries, Final: true)), Timeout, ct);
			var paths = Read<SyncNeedPayload>(need).Paths;

			Assert.Equal("My_Repo", repo);
			Assert.Equal(["src/app.cs", "assets/big.bin"], paths);
			foreach (var path in paths)
			{
				var content = files[path];
				for (var offset = 0; offset < content.Length || offset == 0; offset += SyncLimits.ChunkSize)
				{
					var length = Math.Min(SyncLimits.ChunkSize, content.Length - offset);
					var last = offset + length == content.Length;
					var chunk = Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(repo, path, offset, SyncData.Encode(content.AsSpan(offset, length)), last, last ? Sha(content) : null));
					if (!last)
					{
						await transport.SendAsync(chunk, ct);
						continue;
					}

					var stored = await transport.RequestAsync(chunk, Timeout, ct);
					Assert.Equal(new SyncStoredPayload(repo, path), Read<SyncStoredPayload>(stored));
					break;
				}
			}
		}

		Assert.False(File.Exists(Path.Combine(repoRoot, "stale.txt")));
		Assert.Equal("same", File.ReadAllText(Path.Combine(repoRoot, "same.txt")));
		Assert.Equal("class App { }", File.ReadAllText(Path.Combine(repoRoot, "src", "app.cs")));
		Assert.Equal(big, File.ReadAllBytes(Path.Combine(repoRoot, "assets", "big.bin")));
	}

	[Fact]
	public async Task Disconnect_DropsUnfinishedUpload()
	{
		var ct = TestContext.Current.CancellationToken;
		var content = new byte[SyncLimits.ChunkSize * 2];
		await using (var transport = new SignalRTransport(Connection()))
		{
			await transport.ConnectAsync(ct);
			await transport.RequestAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("r")), Timeout, ct);
			await transport.RequestAsync(Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload("r", [new("a.bin", content.Length, Sha(content))], true)), Timeout, ct);
			await transport.SendAsync(Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload("r", "a.bin", 0, SyncData.Encode(content.AsSpan(0, SyncLimits.ChunkSize)), false)), ct);

			// A request after the chunk: once it is answered, the chunk (same connection, in order) has been written.
			await transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }), Timeout, ct);
			Assert.Single(Directory.GetFiles(Path.Combine(_mirror, "r"), "a.bin.*.aicp-tmp"));
		}

		await WaitUntilAsync(() => Directory.GetFiles(Path.Combine(_mirror, "r"), "*.aicp-tmp").Length == 0, ct);
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

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
	{
		for (var i = 0; i < 600 && !condition(); i++)
		{
			await Task.Delay(50, ct);
		}

		Assert.True(condition());
	}

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
					ws.ConfigureRequest = r => r.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token();
					return await ws.ConnectAsync(ctx.Uri, ct);
				};
			})
			.Build();
	}
}
