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
using Microsoft.Net.Http.Headers;

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

	[Fact]
	public async Task ServerEdit_PushedFetchedAcked_OverSignalR()
	{
		var ct = TestContext.Current.CancellationToken;
		var original = Encoding.UTF8.GetBytes("v1");
		var edited = Encoding.UTF8.GetBytes("v2 edited on the server");
		var pushed = new TaskCompletionSource<SyncRemotePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
		var pushes = 0;

		await using (var transport = new SignalRTransport(Connection()))
		{
			transport.Received += e =>
			{
				if (e.Type != MessageTypes.SyncRemote)
				{
					return;
				}

				var payload = Read<SyncRemotePayload>(e);
				if (payload.Changes.Any(c => c.Sha256 == Sha(edited)))
				{
					Interlocked.Increment(ref pushes);
					pushed.TrySetResult(payload);
				}
			};
			await transport.ConnectAsync(ct);

			var repo = Read<SyncOpenPayload>(await transport.RequestAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("r")), Timeout, ct)).Repo;
			var need = await transport.RequestAsync(Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(repo, [new("a.txt", original.Length, Sha(original))], Final: true)), Timeout, ct);
			Assert.Equal(["a.txt"], Read<SyncNeedPayload>(need).Paths);
			await transport.RequestAsync(Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(repo, "a.txt", 0, SyncData.Encode(original), true, Sha(original))), Timeout, ct);

			await File.WriteAllBytesAsync(Path.Combine(_mirror, repo, "a.txt"), edited, ct);
			var remote = await pushed.Task.WaitAsync(Timeout, ct);
			var change = Assert.Single(remote.Changes);
			Assert.Equal(new RemoteChange("a.txt", Sha(edited), edited.Length, Sha(original)), change);

			var data = Read<SyncDataPayload>(await transport.RequestAsync(Envelope.Create(MessageTypes.SyncFetch, new SyncFetchPayload(repo, "a.txt", 0)), Timeout, ct));
			Assert.True(data.Last);
			Assert.Equal(edited, SyncData.Decode(data.Data));
			Assert.Equal(Sha(edited), data.Sha256);

			var ack = Read<SyncAckPayload>(await transport.RequestAsync(Envelope.Create(MessageTypes.SyncAck, new SyncAckPayload(repo, "a.txt", Sha(edited))), Timeout, ct));
			Assert.Equal(new SyncAckPayload(repo, "a.txt", Sha(edited)), ack);

			var again = await transport.RequestAsync(Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(repo, [new("a.txt", edited.Length, Sha(edited))], Final: true)), Timeout, ct);
			Assert.Empty(Read<SyncNeedPayload>(again).Paths);
			Assert.Equal(1, Volatile.Read(ref pushes));
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _factory.DisposeAsync();
		await TestFolder.DeleteAsync(_testRoot);
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
