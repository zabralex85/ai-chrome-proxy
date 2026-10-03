namespace AiChromeProxy.Client.Sync;

public enum FileSyncState
{
	/// <summary>The mirror has this content.</summary>
	Synced,

	/// <summary>New or changed; waiting for upload.</summary>
	Pending,

	/// <summary>Larger than <see cref="AiChromeProxy.Domain.Sync.SyncLimits.MaxFileSize"/>; not synced.</summary>
	TooLarge,

	/// <summary>Could not be synced (<see cref="SyncFile.Error"/> says why); retried on the next scan.</summary>
	Error,
}

/// <summary>A file of the picked folder as the explorer shows it (excluded files are never listed).</summary>
public sealed record SyncFile(string Path, long Size, string? Sha256, FileSyncState State, string? Error = null);
