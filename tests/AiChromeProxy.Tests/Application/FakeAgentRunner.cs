using System.Threading.Channels;
using AiChromeProxy.Application.Chat;

namespace AiChromeProxy.Tests.Application;

/// <summary>Starts <see cref="FakeAgentProcess"/>es (read them from <see cref="Started"/>), or throws <see cref="StartError"/>.</summary>
public sealed class FakeAgentRunner : IAgentRunner
{
	public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

	/// <summary>Thrown by <see cref="StartAsync"/> when set.</summary>
	public Exception? StartError { get; set; }

	/// <summary>When set, <see cref="StartAsync"/> waits for it (or for the run's cancellation) before starting.</summary>
	public Task? StartGate { get; set; }

	/// <summary>Every started process, in order.</summary>
	public Channel<FakeAgentProcess> Started { get; } = Channel.CreateUnbounded<FakeAgentProcess>();

	public async Task<IAgentProcess> StartAsync(AgentRun run, CancellationToken ct)
	{
		if (StartGate is not null)
		{
			await StartGate.WaitAsync(ct);
		}

		if (StartError is not null)
		{
			throw StartError;
		}

		var process = new FakeAgentProcess(run);
		Started.Writer.TryWrite(process);
		return process;
	}

	/// <summary>The next started process (waits up to 10 seconds).</summary>
	public async Task<FakeAgentProcess> NextAsync()
	{
		using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
		{
			return await Started.Reader.ReadAsync(timeout.Token);
		}
	}
}
