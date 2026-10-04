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

	/// <summary>Server changes written to (or deleted from) the folder in one go (count and the first names).</summary>
	Received,

	/// <summary>A file changed here and on the server since they last agreed.</summary>
	Conflict,

	/// <summary>A file or folder created or renamed in the tree.</summary>
	TreeAction,
}

/// <summary>One entry of the "Actions history" panel.</summary>
/// <param name="Id">Unique per engine (the render key: two entries may have the same time, kind and text).</param>
public sealed record SyncActivity(DateTimeOffset At, SyncActivityKind Kind, string Text, long Id);
