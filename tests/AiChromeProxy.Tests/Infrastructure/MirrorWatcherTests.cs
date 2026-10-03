using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Infrastructure;

/// <summary>The real <see cref="FileSystemWatcher"/> on a temp folder; the quiet period runs on a fake clock.</summary>
public sealed class MirrorWatcherTests : IDisposable
{
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FakeTimeProvider _time = new();
	private readonly ListLogger<MirrorWatcher> _logger = new();
	private readonly MirrorWatcher _watcher;
	private readonly List<IReadOnlyCollection<string>?> _calls = [];

	public MirrorWatcherTests() =>
		_watcher = new MirrorWatcher(Options.Create(new MirrorOptions { Root = _root }), _time, _logger);

	public void Dispose() => Directory.Delete(_root, recursive: true);

	[Fact]
	public async Task Events_CoalescedUntilQuiet()
	{
		using (var watch = Start(Record))
		{
			File.WriteAllText(Path.Combine(_root, "r", "a.txt"), "a");
			Directory.CreateDirectory(Path.Combine(_root, "r", "d"));
			File.WriteAllText(Path.Combine(_root, "r", "d", "B.txt"), "b");
			await WaitForPendingAsync(watch, "a.txt", "d/B.txt");

			Assert.Empty(_calls);
			_time.Advance(TimeSpan.FromMilliseconds(500));

			var paths = Assert.Single(_calls)!;
			Assert.Contains("a.txt", paths);
			Assert.Contains("d/B.txt", paths);
			Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
		}
	}

	[Fact]
	public async Task TempFiles_LeftOut()
	{
		using (var watch = Start(Record))
		{
			File.WriteAllText(Path.Combine(_root, "r", "x.txt.0123456789.aicp-tmp"), "t");
			File.WriteAllText(Path.Combine(_root, "r", "x.txt"), "x");
			await WaitForPendingAsync(watch, "x.txt");

			_time.Advance(TimeSpan.FromMilliseconds(500));

			Assert.Equal(["x.txt"], Assert.Single(_calls)!);
		}
	}

	[Fact]
	public async Task Rename_AddsOldAndNew()
	{
		Directory.CreateDirectory(Path.Combine(_root, "r"));
		File.WriteAllText(Path.Combine(_root, "r", "old.txt"), "o");
		using (var watch = Start(Record))
		{
			File.Move(Path.Combine(_root, "r", "old.txt"), Path.Combine(_root, "r", "new.txt"));
			await WaitForPendingAsync(watch, "old.txt", "new.txt");

			_time.Advance(TimeSpan.FromMilliseconds(500));

			var paths = Assert.Single(_calls)!;
			Assert.Contains("old.txt", paths);
			Assert.Contains("new.txt", paths);
		}
	}

	[Fact]
	public async Task Dispose_StopsCallbacks()
	{
		var watch = Start(Record);
		File.WriteAllText(Path.Combine(_root, "r", "a.txt"), "a");
		await WaitForPendingAsync(watch, "a.txt");

		watch.Dispose();
		_time.Advance(TimeSpan.FromSeconds(5));
		File.WriteAllText(Path.Combine(_root, "r", "b.txt"), "b");
		await Task.Delay(200, TestContext.Current.CancellationToken);
		_time.Advance(TimeSpan.FromSeconds(5));

		Assert.Empty(_calls);
	}

	[Fact]
	public async Task CallbackException_IsLoggedNotThrown()
	{
		using (var watch = Start(_ => throw new InvalidOperationException("boom")))
		{
			File.WriteAllText(Path.Combine(_root, "r", "a.txt"), "a");
			await WaitForPendingAsync(watch, "a.txt");

			_time.Advance(TimeSpan.FromMilliseconds(500));

			Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error);
		}
	}

	[Fact]
	public void Overflow_ReportsNull()
	{
		using (var watch = Start(Record))
		{
			watch.Overflow();
			_time.Advance(TimeSpan.FromMilliseconds(500));

			Assert.Null(Assert.Single(_calls));
		}
	}

	/// <summary>Polls until the watch holds exactly these paths (events arrive on another thread), at most 5 s.</summary>
	private static async Task WaitForPendingAsync(MirrorWatcher.RepoWatch watch, params string[] expected)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!expected.All(p => watch.Pending.Contains(p, StringComparer.OrdinalIgnoreCase)))
		{
			Assert.True(DateTime.UtcNow < deadline, "The watcher did not see the events in 5 s.");
			await Task.Delay(20, TestContext.Current.CancellationToken);
		}
	}

	private MirrorWatcher.RepoWatch Start(Func<IReadOnlyCollection<string>?, Task> changed) => (MirrorWatcher.RepoWatch)_watcher.Watch("r", changed);

	private Task Record(IReadOnlyCollection<string>? paths)
	{
		lock (_calls)
		{
			_calls.Add(paths);
		}

		return Task.CompletedTask;
	}
}
