namespace AiChromeProxy.Domain.Chat;

/// <summary>Values of <see cref="ChatApprovePayload.Decision"/> and of the <c>permissionResolved</c> event's <c>decision</c>.</summary>
public static class ChatDecisions
{
	public const string Allow = "allow";

	public const string AllowAlways = "allowAlways";

	public const string Deny = "deny";
}
