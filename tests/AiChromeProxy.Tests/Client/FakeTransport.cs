using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Client;

/// <summary>In-memory <see cref="ITransport"/>: records what is sent and answers through <see cref="Reply"/>.</summary>
public sealed class FakeTransport : ITransport
{
	private readonly Lock _gate = new();
	private int _pendingReplies;
	private int _connection;

	public event Action<TransportState>? StateChanged;

	public event Action<Envelope>? Received;

	public TransportState State { get; private set; } = TransportState.Disconnected;

	public List<Envelope> Sent { get; } = [];

	/// <summary>Answer to each sent envelope (null: no answer). Runs before <see cref="SendAsync"/> completes, like a fast server.</summary>
	public Func<Envelope, Task<Envelope?>>? Reply { get; set; }

	/// <summary>The first this many <see cref="ConnectAsync"/> calls fail.</summary>
	public int FailConnects { get; set; }

	public int ConnectAttempts { get; private set; }

	/// <summary>
	/// When positive, <see cref="SendAsync"/> returns at once and the reply arrives this much later, like a server behind a slow link
	/// (<see cref="Reply"/> still runs in send order). A reply still on its way when the connection drops is lost.
	/// </summary>
	public TimeSpan ReplyDelay { get; set; }

	/// <summary>The most delayed replies that were on their way at once.</summary>
	public int MaxPendingReplies { get; private set; }

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
		var connection = Volatile.Read(ref _connection);
		if (Reply is not null && await Reply(envelope) is { } reply)
		{
			if (ReplyDelay > TimeSpan.Zero)
			{
				_ = DeliverLaterAsync(reply, connection);
			}
			else
			{
				Received?.Invoke(reply);
			}
		}
	}

	public void SetState(TransportState state)
	{
		if (state != TransportState.Connected)
		{
			Interlocked.Increment(ref _connection);
		}

		State = state;
		StateChanged?.Invoke(state);
	}

	public void Push(Envelope envelope) => Received?.Invoke(envelope);

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	private async Task DeliverLaterAsync(Envelope reply, int connection)
	{
		lock (_gate)
		{
			MaxPendingReplies = Math.Max(MaxPendingReplies, ++_pendingReplies);
		}

		await Task.Delay(ReplyDelay);
		lock (_gate)
		{
			_pendingReplies--;
		}

		// The reply to a message sent before the connection dropped is lost.
		if (connection == Volatile.Read(ref _connection))
		{
			Received?.Invoke(reply);
		}
	}
}
