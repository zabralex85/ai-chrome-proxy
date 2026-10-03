using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Sync;

/// <summary>Routes one sync message type to the sender's <see cref="SyncSession"/>; registered once per type in <see cref="Types"/>.</summary>
public sealed class SyncHandler(string type, SyncSessions sessions) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncDelta, MessageTypes.SyncChunk, MessageTypes.SyncFetch, MessageTypes.SyncAck];

	public string Type => type;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct) =>
		sessions.Get(context.ConnectionId).HandleAsync(request, context, ct);
}
