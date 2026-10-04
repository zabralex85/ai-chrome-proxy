namespace AiChromeProxy.Client.Chat;

/// <summary>A pending permission request: the card shows <paramref name="Tool"/> and <paramref name="Summary"/>.</summary>
/// <param name="RunId">The run that asks.</param>
/// <param name="RequestId">Answers go to <c>chat.approve</c> with this id.</param>
/// <param name="Tool">The tool (e.g. Bash).</param>
/// <param name="Summary">What it would do (e.g. the command).</param>
public sealed record ChatApproval(string RunId, string RequestId, string Tool, string Summary);
