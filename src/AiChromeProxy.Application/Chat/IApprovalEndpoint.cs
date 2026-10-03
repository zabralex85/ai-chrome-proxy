namespace AiChromeProxy.Application.Chat;

/// <summary>Where Claude Code asks for approvals: the Server's loopback MCP endpoint.</summary>
public interface IApprovalEndpoint
{
	/// <summary>Gets the endpoint's URL, or null when there is none (then <c>ask</c> runs without the approval tool and only accepts edits).</summary>
	string? Url { get; }
}
