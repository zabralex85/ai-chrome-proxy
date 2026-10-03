using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Application;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Client;

/// <summary>
/// The real server-side sync (router, sessions, file-system mirror in a temp folder) behind a <see cref="FakeTransport"/>:
/// the client engine is tested end to end without SignalR or a browser.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
	private readonly SyncSessions _sessions;
	private readonly EnvelopeRouter _router;
	private int _connection = 1;

	public LoopbackServer()
	{
		var store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = MirrorRoot }));
		_sessions = new SyncSessions(store, Projects, new ListLogger<SyncSession>(), TimeProvider.System, new FakeMirrorWatcher());
		_router = new EnvelopeRouter([.. SyncHandler.Types.Select(t => new SyncHandler(t, _sessions))]);
		Transport.Reply = ReplyAsync;
		Transport.SetState(TransportState.Connected);
	}

	public string MirrorRoot { get; } = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));

	public FakeTransport Transport { get; } = new();

	/// <summary>The server's bases, baseline flags and project settings.</summary>
	public MemoryProjectStore Projects { get; } = new();

	public string ConnectionId => $"conn-{_connection}";

	public string PathOf(string repo, string path) => Path.Combine(MirrorRoot, repo, path);

	/// <summary>Like SignalR: Reconnecting, a new connection id (the old session is gone on the server), Connected.</summary>
	public void Reconnect()
	{
		Transport.SetState(TransportState.Reconnecting);
		_sessions.Close(ConnectionId);
		_connection++;
		Transport.SetState(TransportState.Connected);
	}

	/// <summary>The server forgets the session without the client noticing (e.g. a server restart between scans).</summary>
	public void DropSession() => _sessions.Close(ConnectionId);

	public void Dispose()
	{
		_sessions.Close(ConnectionId);
		if (Directory.Exists(MirrorRoot))
		{
			Directory.Delete(MirrorRoot, recursive: true);
		}
	}

	private async Task<Envelope?> ReplyAsync(Envelope request)
	{
		try
		{
			return await _router.RouteAsync(request, new EnvelopeContext(ConnectionId, null, (_, _) => Task.CompletedTask), CancellationToken.None);
		}
		catch (Exception)
		{
			return EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}
	}
}
