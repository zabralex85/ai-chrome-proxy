namespace AiChromeProxy.Client.Sync;

/// <summary>Files under a folder; <paramref name="Truncated"/> when the walk stopped at its limits, so <paramref name="Count"/> is a lower bound.</summary>
public sealed record FileCount(int Count, bool Truncated);
