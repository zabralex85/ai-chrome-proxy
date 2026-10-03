namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// <c>chat.event</c> (also an item of <c>chat.events</c>): one record for every <see cref="ChatEventKinds"/>;
/// fields a kind does not use stay null. <paramref name="Seq"/> increases per session.
/// </summary>
public sealed record ChatEvent(
	string SessionId,
	string RunId,
	long Seq,
	string Kind,
	string? Text = null,
	string? ToolId = null,
	string? Name = null,
	string? Summary = null,
	bool? IsError = null,
	string? RequestId = null,
	string? Decision = null,
	bool? Ok = null,
	decimal? CostUsd = null,
	long? DurationMs = null,
	string? Error = null);
