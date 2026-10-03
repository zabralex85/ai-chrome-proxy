using AiChromeProxy.Client.Transport;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiChromeProxy.Tests.Client;

public sealed class ReconnectTests
{
	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 2)]
	[InlineData(2, 5)]
	[InlineData(3, 10)]
	[InlineData(4, 30)]
	[InlineData(1000, 30)]
	public void Policy_NeverGivesUp(long previousRetries, int seconds)
	{
		var delay = new ForeverRetryPolicy().NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetries, ElapsedTime = TimeSpan.FromHours(5) });

		Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
	}

	[Fact]
	public async Task ConnectForever_RetriesWithPolicyDelays_UntilConnected()
	{
		var transport = new FakeTransport { FailConnects = 5 };
		var delays = new List<TimeSpan>();

		await transport.ConnectForeverAsync(
			(d, _) =>
			{
				delays.Add(d);
				return Task.CompletedTask;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(6, transport.ConnectAttempts);
		Assert.Equal([1, 2, 5, 10, 30], delays.Select(d => (int)d.TotalSeconds));
		Assert.Equal(TransportState.Connected, transport.State);
	}

	[Fact]
	public async Task ConnectForever_Cancelled_Stops()
	{
		var transport = new FakeTransport { FailConnects = int.MaxValue };
		using (var cts = new CancellationTokenSource())
		{
			var task = transport.ConnectForeverAsync(
				async (d, ct) =>
				{
					await cts.CancelAsync();
					await Task.Delay(d, ct);
				},
				cts.Token);

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
			Assert.Equal(1, transport.ConnectAttempts);
		}
	}
}
