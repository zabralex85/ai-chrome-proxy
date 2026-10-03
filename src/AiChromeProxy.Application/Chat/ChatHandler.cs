using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>Routes one chat message type (see <see cref="Types"/>; registered once per type) to the <see cref="ChatService"/>.</summary>
public sealed class ChatHandler(string type, ChatService chat) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.ChatOpen, MessageTypes.ChatHistory, MessageTypes.ChatSend, MessageTypes.ChatCancel, MessageTypes.ChatApprove];

	public string Type => type;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var reply = type switch
		{
			MessageTypes.ChatOpen => Envelope.Create(MessageTypes.ChatSessions, chat.Open(Read<ChatOpenPayload>(request).Repo, context)),
			MessageTypes.ChatHistory => History(request),
			MessageTypes.ChatSend => Envelope.Create(MessageTypes.ChatStarted, chat.Send(Read<ChatSendPayload>(request), context)),
			MessageTypes.ChatCancel => Cancel(request),
			_ => Approve(request),
		};

		return Task.FromResult<Envelope?>(reply with { CorrelationId = request.CorrelationId });
	}

	private static T Read<T>(Envelope request)
		where T : class
	{
		try
		{
			return request.Payload.Deserialize<T>(JsonSerializerOptions.Web) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Invalid {request.Type} payload.");
		}
	}

	private Envelope History(Envelope request)
	{
		var payload = Read<ChatHistoryPayload>(request);
		return Envelope.Create(MessageTypes.ChatEvents, chat.History(payload.SessionId, payload.AfterSeq));
	}

	private Envelope Cancel(Envelope request)
	{
		var payload = Read<ChatCancelPayload>(request);
		chat.Cancel(payload.RunId);
		return Envelope.Create(MessageTypes.ChatCancel, payload);
	}

	private Envelope Approve(Envelope request)
	{
		var payload = Read<ChatApprovePayload>(request);
		chat.Approve(payload);
		return Envelope.Create(MessageTypes.ChatApprove, payload);
	}
}
