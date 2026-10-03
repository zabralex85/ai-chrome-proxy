using AiChromeProxy.Client.Shell;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Client;

public sealed class RenderCoalescerTests
{
	private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);

	private readonly FakeTimeProvider _clock = new();
	private int _renders;

	[Fact]
	public void FirstRequest_RendersAtOnce()
	{
		using (var coalescer = Create())
		{
			coalescer.Request();

			Assert.Equal(1, _renders);
		}
	}

	[Fact]
	public void BurstWithinTheInterval_OneTrailingRender()
	{
		using (var coalescer = Create())
		{
			coalescer.Request();
			for (var i = 0; i < 5; i++)
			{
				coalescer.Request();
			}

			_clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
			Assert.Equal(1, _renders);

			_clock.Advance(TimeSpan.FromMilliseconds(1));
			Assert.Equal(2, _renders);

			_clock.Advance(TimeSpan.FromSeconds(1));
			Assert.Equal(2, _renders);
		}
	}

	[Fact]
	public void AfterAQuietInterval_RendersAtOnceAgain()
	{
		using (var coalescer = Create())
		{
			coalescer.Request();
			_clock.Advance(Interval);

			coalescer.Request();

			Assert.Equal(2, _renders);
		}
	}

	[Fact]
	public void TrailingRender_StartsANewInterval()
	{
		using (var coalescer = Create())
		{
			coalescer.Request();
			coalescer.Request();
			_clock.Advance(Interval);
			Assert.Equal(2, _renders);

			coalescer.Request();
			Assert.Equal(2, _renders);

			_clock.Advance(Interval);
			Assert.Equal(3, _renders);
		}
	}

	[Fact]
	public void Dispose_DropsThePendingRenderAndLaterRequests()
	{
		var coalescer = Create();
		coalescer.Request();
		coalescer.Request();

		coalescer.Dispose();
		_clock.Advance(TimeSpan.FromSeconds(1));
		coalescer.Request();

		Assert.Equal(1, _renders);
	}

	private RenderCoalescer Create() => new(_clock, Interval, () => _renders++);
}
