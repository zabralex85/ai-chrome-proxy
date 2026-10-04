using AiChromeProxy.Application.Chat;

namespace AiChromeProxy.Server.Chat;

/// <summary>The Server's approval endpoint (<see cref="ApprovalMcp"/>), reached by the agent over loopback.</summary>
public sealed record ApprovalEndpoint(string? Url) : IApprovalEndpoint;
