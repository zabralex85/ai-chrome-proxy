namespace AiChromeProxy.Client.Tree;

/// <summary>Whether a menu item can be used, and when not, why (shown as its tooltip).</summary>
public sealed record MenuState(bool Enabled, string? Reason = null);
