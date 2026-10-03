namespace AiChromeProxy.Domain;

public static class MessageTypes
{
	public const string Ping = "ping";
	public const string Pong = "pong";
	public const string Error = "error";

	/// <summary>Client → server: start a sync session for a folder (<c>Sync.SyncOpenPayload</c>); reply <see cref="SyncOpened"/>.</summary>
	public const string SyncOpen = "sync.open";

	/// <summary>Server → client: the session is open (<c>Sync.SyncOpenPayload</c> with the sanitized repo name).</summary>
	public const string SyncOpened = "sync.opened";

	/// <summary>Client → server: one page of the full manifest (<c>Sync.SyncManifestPayload</c>); reply <see cref="SyncNeed"/>.</summary>
	public const string SyncManifest = "sync.manifest";

	/// <summary>Server → client: paths to upload (<c>Sync.SyncNeedPayload</c>).</summary>
	public const string SyncNeed = "sync.need";

	/// <summary>Client → server: part of a needed file (<c>Sync.SyncChunkPayload</c>); the last one is answered with <see cref="SyncStored"/>.</summary>
	public const string SyncChunk = "sync.chunk";

	/// <summary>Server → client: a file is in the mirror (<c>Sync.SyncStoredPayload</c>).</summary>
	public const string SyncStored = "sync.stored";

	/// <summary>Client → server: changes since the last manifest (<c>Sync.SyncDeltaPayload</c>); reply <see cref="SyncNeed"/>.</summary>
	public const string SyncDelta = "sync.delta";
}
