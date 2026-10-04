namespace AiChromeProxy.Domain.Chat;

/// <summary>Size limits of the chat wire contract.</summary>
public static class ChatLimits
{
	/// <summary>Longest user message (<c>chat.send</c>) and longest <c>prompt</c>/<c>text</c>/<c>message</c> part, in UTF-16 characters.</summary>
	public const int MaxTextChars = 16_000;

	/// <summary>Longest serialized (<see cref="System.Text.Json.JsonSerializerOptions.Web"/>) event or events page, in bytes; fits SignalR's 32 KB limit.</summary>
	public const int MaxEventBytes = 24_000;

	/// <summary>Longest event <c>summary</c>, in UTF-8 bytes.</summary>
	public const int ToolSummaryBytes = 2_048;

	/// <summary>
	/// Longest <c>permission</c> summary, serialized as a JSON string (<see cref="System.Text.Json.JsonSerializerOptions.Web"/>), in bytes:
	/// the card shows the whole request whenever it fits the event.
	/// </summary>
	public const int PermissionSummaryBytes = 20_000;

	/// <summary>Longest event <c>name</c> (a tool), in UTF-8 bytes.</summary>
	public const int MaxNameBytes = 200;
}
