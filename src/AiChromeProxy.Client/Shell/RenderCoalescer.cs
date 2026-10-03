namespace AiChromeProxy.Client.Shell;

/// <summary>
/// Turns bursts of change notifications into at most one render per <c>interval</c>: a change after a quiet interval renders
/// at once, the changes that follow share one render at the end of the interval.
/// </summary>
public sealed class RenderCoalescer(TimeProvider time, TimeSpan interval, Action render) : IDisposable
{
	private readonly Lock _gate = new();
	private long? _lastRender;
	private ITimer? _pending;
	private bool _disposed;

	public void Request()
	{
		lock (_gate)
		{
			if (_disposed || _pending is not null)
			{
				return;
			}

			var wait = _lastRender is { } last ? interval - time.GetElapsedTime(last) : TimeSpan.Zero;
			if (wait > TimeSpan.Zero)
			{
				_pending = time.CreateTimer(_ => Fire(), null, wait, Timeout.InfiniteTimeSpan);
				return;
			}

			_lastRender = time.GetTimestamp();
		}

		render();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			_pending?.Dispose();
			_pending = null;
		}
	}

	private void Fire()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_pending?.Dispose();
			_pending = null;
			_lastRender = time.GetTimestamp();
		}

		render();
	}
}
