using AiChromeProxy.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiChromeProxy.Client.Transport;

/// <summary><see cref="ITransport"/> over a SignalR hub exposing Send(Envelope) / Receive(Envelope).</summary>
public sealed class SignalRTransport : ITransport
{
	public const string SendMethod = "Send";
	public const string ReceiveMethod = "Receive";

	private readonly HubConnection _connection;

	/// <param name="connection">Built by the caller (URL, auto-reconnect, test handlers).</param>
	public SignalRTransport(HubConnection connection)
	{
		_connection = connection;
		_connection.On<Envelope>(ReceiveMethod, e => Received?.Invoke(e));
		_connection.Reconnecting += _ => SetState(TransportState.Reconnecting);
		_connection.Reconnected += _ => SetState(TransportState.Connected);
		_connection.Closed += _ => SetState(TransportState.Disconnected);
	}

	public event Action<TransportState>? StateChanged;

	public event Action<Envelope>? Received;

	public TransportState State { get; private set; } = TransportState.Disconnected;

	public async Task ConnectAsync(CancellationToken ct = default)
	{
		await SetState(TransportState.Connecting);
		try
		{
			await _connection.StartAsync(ct);
		}
		catch
		{
			await SetState(TransportState.Disconnected);
			throw;
		}

		await SetState(TransportState.Connected);
	}

	public Task SendAsync(Envelope envelope, CancellationToken ct = default) =>
		_connection.SendAsync(SendMethod, envelope, ct);

	public ValueTask DisposeAsync() => _connection.DisposeAsync();

	private Task SetState(TransportState state)
	{
		State = state;
		StateChanged?.Invoke(state);
		return Task.CompletedTask;
	}
}
