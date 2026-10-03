using AiChromeProxy.Application.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Sync;

/// <summary><see cref="IMirrorWatcher"/> on <see cref="FileSystemWatcher"/>: events are collected until 500 ms pass without a new one.</summary>
public sealed class MirrorWatcher(IOptions<MirrorOptions> options, TimeProvider time, ILogger<MirrorWatcher> logger) : IMirrorWatcher
{
	private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

	/// <summary>The longest a steady stream of events (build output, logs) can hold back the callback.</summary>
	private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

	private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root));

	public IDisposable Watch(string repo, Func<IReadOnlyCollection<string>?, Task> changed)
	{
		var folder = Path.Combine(_root, repo);
		Directory.CreateDirectory(folder);
		return new RepoWatch(folder, changed, time, logger);
	}

	/// <summary>One watched repo folder.</summary>
	internal sealed class RepoWatch : IDisposable
	{
		private readonly string _folder;
		private readonly Func<IReadOnlyCollection<string>?, Task> _changed;
		private readonly ILogger _logger;
		private readonly object _lock = new();
		private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
		private readonly FileSystemWatcher _watcher;
		private readonly ITimer _timer;
		private readonly TimeProvider _time;
		private DateTimeOffset? _first;
		private bool _overflow;
		private bool _disposed;

		public RepoWatch(string folder, Func<IReadOnlyCollection<string>?, Task> changed, TimeProvider time, ILogger logger)
		{
			_folder = folder;
			_changed = changed;
			_logger = logger;
			_time = time;
			_timer = time.CreateTimer(Fire, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			_watcher = new FileSystemWatcher(folder)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
				InternalBufferSize = 64 * 1024,
			};
			_watcher.Changed += (_, e) =>
			{
				// A folder's timestamp changes whenever an entry in it is created, deleted or renamed; its files report themselves.
				if (!Directory.Exists(e.FullPath))
				{
					Add(e.FullPath);
				}
			};
			_watcher.Created += (_, e) => Add(e.FullPath);
			_watcher.Deleted += (_, e) => Add(e.FullPath);
			_watcher.Renamed += (_, e) => Add(e.OldFullPath, e.FullPath);
			_watcher.Error += (_, e) =>
			{
				_logger.LogWarning(e.GetException(), "Mirror watcher lost events in {Folder}; every file will be checked", _folder);
				Overflow();
			};
			_watcher.EnableRaisingEvents = true;
		}

		/// <summary>The paths collected since the last callback.</summary>
		internal IReadOnlyCollection<string> Pending
		{
			get
			{
				lock (_lock)
				{
					return [.. _pending];
				}
			}
		}

		public void Dispose()
		{
			lock (_lock)
			{
				_disposed = true;
			}

			_watcher.EnableRaisingEvents = false;
			_watcher.Dispose();
			_timer.Dispose();
		}

		/// <summary>Events were lost: the next callback gets null.</summary>
		internal void Overflow()
		{
			lock (_lock)
			{
				_overflow = true;
				Arm();
			}
		}

		private void Add(params string[] fullPaths)
		{
			lock (_lock)
			{
				foreach (var full in fullPaths)
				{
					var relative = Path.GetRelativePath(_folder, full).Replace(Path.DirectorySeparatorChar, '/');
					if (relative is "." || relative.StartsWith("../", StringComparison.Ordinal) || relative.EndsWith(".aicp-tmp", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					_pending.Add(relative);
				}

				Arm();
			}
		}

		/// <summary>Restarts the quiet period, but never past <see cref="MaxWait"/> after the first event; the caller holds the lock.</summary>
		private void Arm()
		{
			if (_disposed)
			{
				return;
			}

			// ponytail: fixed 3 s ceiling; make it configurable if a repo needs a different latency.
			var now = _time.GetUtcNow();
			_first ??= now;
			var left = _first.Value + MaxWait - now;
			_timer.Change(left < Quiet ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : Quiet, Timeout.InfiniteTimeSpan);
		}

		private void Fire(object? state)
		{
			string[]? paths;
			lock (_lock)
			{
				if (_disposed || (!_overflow && _pending.Count == 0))
				{
					return;
				}

				paths = _overflow ? null : [.. _pending];
				_overflow = false;
				_first = null;
				_pending.Clear();
			}

			_ = RunAsync(paths);
		}

		private async Task RunAsync(string[]? paths)
		{
			try
			{
				await _changed(paths);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Mirror watcher: handling changes in {Folder} failed", _folder);
			}
		}
	}
}
