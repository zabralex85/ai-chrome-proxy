using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Application;

/// <summary>One watch per repo for all sessions, and the fan-out of its changes.</summary>
public sealed class SyncSessionsWatchTests : IDisposable
{
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FakeMirrorWatcher _watcher = new();
	private readonly MemoryProjectStore _projects = new();
	private readonly SyncSessions _sessions;
	private readonly Dictionary<string, List<Envelope>> _pushed = [];

	public SyncSessionsWatchTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "r"));
		Directory.CreateDirectory(Path.Combine(_root, "s"));
		_sessions = new SyncSessions(
			new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root })),
			_projects,
			new ListLogger<SyncSession>(),
			TimeProvider.System,
			_watcher);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose()
	{
		foreach (var id in _pushed.Keys)
		{
			_sessions.Close(id);
		}

		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public async Task OneWatchPerRepo_DisposedWhenLastSessionCloses()
	{
		await OpenAsync("c1", "r");
		await OpenAsync("c2", "r");
		Assert.Equal(["r"], _watcher.Started);

		_sessions.Close("c1");
		Assert.Equal(["r"], _watcher.Active);

		_sessions.Close("c2");
		Assert.Empty(_watcher.Active);
	}

	[Fact]
	public async Task ReopeningTheSameRepo_KeepsOneWatch()
	{
		await OpenAsync("c1", "r");
		await OpenAsync("c1", "r");

		Assert.Equal(["r"], _watcher.Started);
		_sessions.Close("c1");
		Assert.Empty(_watcher.Active);
	}

	[Fact]
	public async Task RepoChange_MovesTheWatch()
	{
		await OpenAsync("c1", "r");
		await OpenAsync("c1", "s");

		Assert.Equal(["r", "s"], _watcher.Started);
		Assert.Equal(["s"], _watcher.Active);
	}

	[Fact]
	public async Task Change_FansOutToSessionsWithThatRepo()
	{
		await OpenAsync("c1", "r");
		await OpenAsync("c2", "r");
		await OpenAsync("c3", "s");
		File.WriteAllText(Path.Combine(_root, "r", "a.txt"), "x");
		File.WriteAllText(Path.Combine(_root, "s", "a.txt"), "x");

		await _watcher.RaiseAsync("r", ["a.txt"]);

		Assert.Single(_pushed["c1"], e => e.Type == MessageTypes.SyncRemote);
		Assert.Single(_pushed["c2"], e => e.Type == MessageTypes.SyncRemote);
		Assert.DoesNotContain(_pushed["c3"], e => e.Type == MessageTypes.SyncRemote);
	}

	private async Task OpenAsync(string connection, string repo)
	{
		var pushed = _pushed.TryGetValue(connection, out var list) ? list : _pushed[connection] = [];
		_projects.SetBaselined(repo);
		await _sessions.Get(connection).HandleAsync(
			Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(repo)),
			new EnvelopeContext(connection, null, (e, _) =>
			{
				pushed.Add(e);
				return Task.CompletedTask;
			}),
			Ct);
	}
}
