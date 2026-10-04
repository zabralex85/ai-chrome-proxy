namespace AiChromeProxy.Client.Chat;

/// <summary>What a <see cref="ChatItem"/> shows.</summary>
public enum ChatItemKind
{
	/// <summary>The user's message.</summary>
	User,

	/// <summary>Claude's text (a block of consecutive messages, or the text streaming in).</summary>
	Assistant,

	/// <summary>A tool call (<see cref="ChatItem.Name"/>, <see cref="ChatItem.Text"/> = its summary); <see cref="ChatItem.Result"/> once the result arrived.</summary>
	Tool,

	/// <summary>A failed run, or a request the server refused (<c>busy</c>, too long, ...).</summary>
	Error,
}

/// <summary>One row of a chat, in display order.</summary>
/// <param name="Kind">What the row is.</param>
/// <param name="RunId">The run it belongs to (empty for a local error line).</param>
/// <param name="Text">The text (a tool's summary for <see cref="ChatItemKind.Tool"/>).</param>
/// <param name="Name">The tool's name.</param>
/// <param name="ToolId">The tool call's id.</param>
/// <param name="Result">The tool result's summary, once it arrived.</param>
/// <param name="IsError">The tool result is an error.</param>
/// <param name="Streaming">The text is still arriving (live deltas); the stored <c>message</c> will replace it.</param>
public sealed record ChatItem(
	ChatItemKind Kind,
	string RunId,
	string Text,
	string? Name = null,
	string? ToolId = null,
	string? Result = null,
	bool IsError = false,
	bool Streaming = false);
