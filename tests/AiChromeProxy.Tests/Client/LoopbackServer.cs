using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Projects;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Application;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Client;

/// <summary>
/// The real server-side sync and chat (router, sessions, file-system mirror in a temp folder, chat service with a scripted agent) behind a <see cref="FakeTransport"/>:
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
		_sessions = new SyncSessions(store, Projects, new ListLogger<SyncSession>(), TimeProvider.System, Watcher);
		Broker = new PermissionBroker(Projects, TimeProvider.System, new ListLogger<PermissionBroker>());
		Chat = new ChatService(Chats, store, Projects, Agent, TimeProvider.System, new ListLogger<ChatService>(), Broker, new ApprovalEndpoint());
		_router = new EnvelopeRouter(
		[
			.. SyncHandler.Types.Select(t => (IEnvelopeHandler)new SyncHandler(t, _sessions)),
			.. ProjectSettingsHandler.Types.Select(t => new ProjectSettingsHandler(t, Projects, _sessions)),
			.. ChatHandler.Types.Select(t => new ChatHandler(t, Chat)),
			.. ClaudeToolsHandler.Types.Select(t => new ClaudeToolsHandler(t, Projects, store, new ToolsProbe(this), TimeProvider.System, new ListLogger<ClaudeToolsHandler>())),
		]);
		Transport.Reply = ReplyAsync;
		Transport.SetState(TransportState.Connected);
	}

	public string MirrorRoot { get; } = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));

	public FakeTransport Transport { get; } = new();

	/// <summary>The server's mirror watcher: <c>RaiseAsync</c> stands for an edit seen on the mirror (its pushes reach <see cref="Transport"/>).</summary>
	public FakeMirrorWatcher Watcher { get; } = new();

	/// <summary>The server's bases, baseline flags and project settings.</summary>
	public MemoryProjectStore Projects { get; } = new();

	/// <summary>The server's chat sessions and events.</summary>
	public MemoryChatStore Chats { get; } = new();

	/// <summary>The server's agent: read started runs from it and script their output.</summary>
	public FakeAgentRunner Agent { get; } = new();

	public ChatService Chat { get; }

	/// <summary>The server's permission broker: a test asks it for an approval the way the approval tool does.</summary>
	public PermissionBroker Broker { get; }

	/// <summary>What <b>Check now</b>'s probe answers for the mirror folder it runs in.</summary>
	public Func<string, (ClaudeToolsSnapshot? Snapshot, string? Error)> Probe { get; set; } = _ => (null, "Check failed: no probe.");

	/// <summary>When set, pushes are lost (a connection that is down): <see cref="Reconnect"/> and a catch-up bring the client back up to date.</summary>
	public bool DropPushes { get; set; }

	public string ConnectionId => $"conn-{_connection}";

	public string PathOf(string repo, string path) => Path.Combine(MirrorRoot, repo, path);

	/// <summary>Like SignalR: Reconnecting, a new connection id (the old session is gone on the server), Connected.</summary>
	public void Reconnect()
	{
		DropPushes = false;
		Transport.SetState(TransportState.Reconnecting);
		_sessions.Close(ConnectionId);
		Chat.Unsubscribe(ConnectionId);
		_connection++;
		Transport.SetState(TransportState.Connected);
	}

	/// <summary>The server forgets the session without the client noticing (e.g. a server restart between scans).</summary>
	public void DropSession() => _sessions.Close(ConnectionId);

	public void Dispose()
	{
		Chat.Dispose();
		_sessions.Close(ConnectionId);
		if (Directory.Exists(MirrorRoot))
		{
			Directory.Delete(MirrorRoot, recursive: true);
		}
	}

	/// <summary>Pushes (<c>sync.remote</c>) reach the client at once, like a reply.</summary>
	private Task PushAsync(Envelope envelope, CancellationToken ct)
	{
		if (!DropPushes)
		{
			Transport.Push(envelope);
		}

		return Task.CompletedTask;
	}

	private async Task<Envelope?> ReplyAsync(Envelope request)
	{
		try
		{
			return await _router.RouteAsync(request, new EnvelopeContext(ConnectionId, null, PushAsync), CancellationToken.None);
		}
		catch (Exception)
		{
			return EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}
	}

	private sealed class ToolsProbe(LoopbackServer server) : IClaudeToolsProbe
	{
		public Task<(ClaudeToolsSnapshot? Snapshot, string? Error)> CheckAsync(string folder) => Task.FromResult(server.Probe(folder));
	}

	private sealed class ApprovalEndpoint : IApprovalEndpoint
	{
		public string? Url => "http://127.0.0.1:5180/mcp/approve";
	}
}
