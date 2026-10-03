using Microsoft.AspNetCore.SignalR.Client;

namespace AiChromeProxy.Client.Transport;

/// <summary>Reconnects forever: 1, 2, 5, 10 s, then every 30 s (SignalR's default policy gives up after about 42 s).</summary>
public sealed class ForeverRetryPolicy : IRetryPolicy
{
	private static readonly TimeSpan[] FirstDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

	public static TimeSpan Steady { get; } = TimeSpan.FromSeconds(30);

	/// <param name="previousAttempts">Failed attempts so far (0 before the first retry).</param>
	public static TimeSpan Delay(long previousAttempts) =>
		previousAttempts < FirstDelays.Length ? FirstDelays[previousAttempts] : Steady;

	public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);
}
