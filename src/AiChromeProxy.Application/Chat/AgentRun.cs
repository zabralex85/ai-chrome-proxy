using System.Globalization;
using System.Text;

namespace AiChromeProxy.Application.Chat;

/// <summary>One agent turn: where it runs, what it is asked and how it may act.</summary>
/// <param name="RepoFolder">Absolute folder the agent runs in (<c>&lt;mirror&gt;\&lt;repo&gt;</c>).</param>
/// <param name="Prompt">The user's message; written to the process's stdin.</param>
/// <param name="ResumeId">Claude's own session id to continue, or null for a new conversation.</param>
/// <param name="Permissions">Setting <c>agentPermissions</c>: <c>ask</c>, <c>all</c> or <c>settings</c>; anything else is treated as <c>ask</c>.</param>
/// <param name="Model">Model name, or null/empty for the CLI's default.</param>
/// <param name="AllowedTools">Rules passed as allowed tools (setting <c>agentAllowedTools</c>).</param>
/// <param name="ApprovalUrl">Approval MCP endpoint of this run, or null when there is none (then <c>ask</c> only accepts edits).</param>
/// <param name="ApprovalToken">The run's bearer token for <paramref name="ApprovalUrl"/>.</param>
/// <param name="DisabledMcpServers">MCP servers turned off in this project (setting <c>agentDisabledMcpServers</c>).</param>
/// <param name="DisabledPlugins">Plugin ids turned off in this project (setting <c>agentDisabledPlugins</c>).</param>
/// <param name="ApprovedMcpServers">
/// Project <c>.mcp.json</c> servers approved in this project (setting <c>agentApprovedMcpServers</c>); <see cref="ChatService"/> passes only those
/// whose entry is unchanged since (<see cref="McpJson.Approved"/>).
/// </param>
/// <remarks><see cref="ToString"/> hides <paramref name="ApprovalToken"/>, so a logged run never shows it.</remarks>
public sealed record AgentRun(
	string RepoFolder,
	string Prompt,
	string? ResumeId = null,
	string Permissions = "ask",
	string? Model = null,
	IReadOnlyList<string>? AllowedTools = null,
	string? ApprovalUrl = null,
	string? ApprovalToken = null,
	IReadOnlyList<string>? DisabledMcpServers = null,
	IReadOnlyList<string>? DisabledPlugins = null,
	IReadOnlyList<string>? ApprovedMcpServers = null)
{
	private bool PrintMembers(StringBuilder builder)
	{
		builder.Append(CultureInfo.InvariantCulture, $"RepoFolder = {RepoFolder}, Prompt = {Prompt}, ResumeId = {ResumeId}, Permissions = {Permissions}, Model = {Model}, ");
		builder.Append(CultureInfo.InvariantCulture, $"AllowedTools = {AllowedTools}, DisabledMcpServers = {DisabledMcpServers}, DisabledPlugins = {DisabledPlugins}, ApprovedMcpServers = {ApprovedMcpServers}, ");
		builder.Append(CultureInfo.InvariantCulture, $"ApprovalUrl = {ApprovalUrl}, ApprovalToken = {(ApprovalToken is null ? string.Empty : "***")}");
		return true;
	}
}
