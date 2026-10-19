using System.Text.Json.Serialization;

namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// <c>chat.send</c>: a user message (at most <see cref="ChatLimits.MaxTextChars"/> characters); no <paramref name="SessionId"/> starts a new session.
/// <paramref name="Attachments"/> lists ids of finished <c>chat.attach</c> uploads (at most <see cref="ChatLimits.MaxAttachments"/>); with one, the text may be empty.
/// </summary>
public sealed record ChatSendPayload(
	string Repo,
	string? SessionId,
	string Text,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Attachments = null);
