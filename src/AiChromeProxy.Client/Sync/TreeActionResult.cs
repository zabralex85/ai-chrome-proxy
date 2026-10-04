namespace AiChromeProxy.Client.Sync;

/// <summary>Outcome of a tree action (create, rename, delete).</summary>
/// <param name="Ok">Whether the folder was changed.</param>
/// <param name="Error">Why it was not, shown to the user; null when it was.</param>
/// <param name="Note">Something to tell the user about a change that worked (for example that the new item is excluded from sync); null otherwise.</param>
public sealed record TreeActionResult(bool Ok, string? Error = null, string? Note = null);
