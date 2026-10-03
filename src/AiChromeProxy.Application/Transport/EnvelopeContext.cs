using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

/// <summary>Who sent an envelope and how to reach them: connection id, Access identity and a channel for replies and pushes.</summary>
public sealed class EnvelopeContext(string connectionId, string? email, Func<Envelope, CancellationToken, Task> send)
{
	public string ConnectionId => connectionId;

	/// <summary>Email claim of the Cloudflare Access token; null when the Access check is disabled (Development).</summary>
	public string? Email => email;

	/// <summary>Sends an envelope to this connection at any time (also after the handler returned).</summary>
	public Task SendAsync(Envelope envelope, CancellationToken ct) => send(envelope, ct);
}
