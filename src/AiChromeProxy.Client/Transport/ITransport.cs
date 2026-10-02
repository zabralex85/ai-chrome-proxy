using AiChromeProxy.Shared;

namespace AiChromeProxy.Client.Transport;

public enum TransportState
{
	Disconnected,
	Connecting,
	Connected,
	Reconnecting,
}

/// <summary>Client side of the single envelope channel; SignalR today, WebRTC later.</summary>
public interface ITransport : IAsyncDisposable
{
	event Action<TransportState>? StateChanged;

	event Action<Envelope>? Received;

	TransportState State { get; }

	Task ConnectAsync(CancellationToken ct = default);

	Task SendAsync(Envelope envelope, CancellationToken ct = default);
}
