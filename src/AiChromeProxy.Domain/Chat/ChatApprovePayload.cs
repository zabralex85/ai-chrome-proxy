namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.approve</c>: answers a <c>permission</c> event (<paramref name="Decision"/> is one of <see cref="ChatDecisions"/>); echoed back.</summary>
public sealed record ChatApprovePayload(string RunId, string RequestId, string Decision);
