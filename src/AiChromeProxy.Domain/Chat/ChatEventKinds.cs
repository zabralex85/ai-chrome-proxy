namespace AiChromeProxy.Domain.Chat;

/// <summary>Values of <see cref="ChatEvent.Kind"/>.</summary>
public static class ChatEventKinds
{
	/// <summary>A streaming text delta (<c>text</c>); not stored.</summary>
	public const string Text = "text";

	/// <summary>The authoritative text of an assistant message (<c>text</c>); replaces the deltas.</summary>
	public const string Message = "message";

	/// <summary>A tool call (<c>toolId</c>, <c>name</c>, <c>summary</c>).</summary>
	public const string Tool = "tool";

	/// <summary>A tool result (<c>toolId</c>, <c>isError</c>, <c>summary</c>).</summary>
	public const string ToolResult = "toolResult";

	/// <summary>A permission request (<c>requestId</c>, <c>name</c>, <c>summary</c>).</summary>
	public const string Permission = "permission";

	/// <summary>A permission request was answered (<c>requestId</c>, <c>decision</c>).</summary>
	public const string PermissionResolved = "permissionResolved";

	/// <summary>The run ended (<c>ok</c>, <c>costUsd</c>, <c>durationMs</c>, <c>error</c>).</summary>
	public const string Result = "result";
}
