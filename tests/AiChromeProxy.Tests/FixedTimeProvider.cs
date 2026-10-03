namespace AiChromeProxy.Tests;

/// <summary>Test clock: wall clock (<see cref="Now"/>) and monotonic clock (<see cref="Elapsed"/>) move independently.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
	public DateTimeOffset Now { get; set; } = now;

	/// <summary>Monotonic reading behind <see cref="GetTimestamp"/>; unaffected by setting <see cref="Now"/> (a wall-clock jump).</summary>
	public TimeSpan Elapsed { get; set; }

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public override DateTimeOffset GetUtcNow() => Now;

	public override long GetTimestamp() => Elapsed.Ticks;

	/// <summary>Real time passing: both clocks move.</summary>
	public void Advance(TimeSpan by)
	{
		Now += by;
		Elapsed += by;
	}
}
