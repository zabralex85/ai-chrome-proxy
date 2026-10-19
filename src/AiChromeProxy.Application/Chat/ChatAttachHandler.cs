using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>Routes <c>chat.attach</c> to <see cref="ChatAttachments"/>; only the last chunk of an upload gets a reply.</summary>
public sealed class ChatAttachHandler(ChatAttachments attachments) : IEnvelopeHandler
{
	public string Type => MessageTypes.ChatAttach;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		ChatAttachPayload payload;
		try
		{
			payload = request.Payload.Deserialize<ChatAttachPayload>(JsonSerializerOptions.Web) ?? throw new EnvelopeException(ErrorCodes.BadRequest, "chat.attach needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "Invalid chat.attach payload.");
		}

		var reply = attachments.Append(payload);
		return Task.FromResult(reply is null ? null : Envelope.Create(MessageTypes.ChatAttached, reply, request.CorrelationId));
	}
}
