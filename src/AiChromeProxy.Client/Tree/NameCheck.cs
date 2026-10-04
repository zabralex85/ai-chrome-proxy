namespace AiChromeProxy.Client.Tree;

/// <summary>Verdict on a name typed into the tree.</summary>
/// <param name="Error">Why the name cannot be used, or null.</param>
/// <param name="Excluded">Whether sync would leave the item out (allowed, but the user is told).</param>
public sealed record NameCheck(string? Error, bool Excluded);
