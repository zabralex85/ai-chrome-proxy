using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Client;

/// <summary>In-memory <see cref="ITransport"/>: records what is sent and answers through <see cref="Reply"/>.</summary>
public sealed class FakeTransport : ITransport
{
	public event Action<TransportState>? StateChanged;

	public event Action<Envelope>? Received;

	public TransportState State { get; private set; } = TransportState.Disconnected;

	public List<Envelope> Sent { get; } = [];

	/// <summary>Answer to each sent envelope (null: no answer). Runs before <see cref="SendAsync"/> completes, like a fast server.</summary>
	public Func<Envelope, Task<Envelope?>>? Reply { get; set; }

	/// <summary>The first this many <see cref="ConnectAsync"/> calls fail.</summary>
	public int FailConnects { get; set; }

	public int ConnectAttempts { get; private set; }

	public int ReceivedHandlers => Received?.GetInvocationList().Length ?? 0;

	public Task ConnectAsync(CancellationToken ct = default)
	{
		ConnectAttempts++;
		if (ConnectAttempts <= FailConnects)
		{
			return Task.FromException(new HttpRequestException("offline"));
		}

		SetState(TransportState.Connected);
		return Task.CompletedTask;
	}

	public async Task SendAsync(Envelope envelope, CancellationToken ct = default)
	{
		Sent.Add(envelope);
		if (Reply is not null && await Reply(envelope) is { } reply)
		{
			Received?.Invoke(reply);
		}
	}

	public void SetState(TransportState state)
	{
		State = state;
		StateChanged?.Invoke(state);
	}

	public void Push(Envelope envelope) => Received?.Invoke(envelope);

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
