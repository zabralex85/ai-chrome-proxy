namespace AiChromeProxy.Client.Sync;

public enum SyncActivityKind
{
	FolderOpened,

	/// <summary>A cycle that has something to send: a full manifest, or a delta with changes.</summary>
	PassStarted,

	/// <summary>Files stored in the mirror during the pass (count and the first names).</summary>
	Uploaded,

	/// <summary>Files deleted from the mirror during the pass (count and the first names).</summary>
	Deleted,

	PassFinished,

	/// <summary>A failed cycle (the server's message) or the files a pass could not upload.</summary>
	Error,

	Reconnected,

	AccessLost,
}

/// <summary>One entry of the "Actions history" panel.</summary>
public sealed record SyncActivity(DateTimeOffset At, SyncActivityKind Kind, string Text);
