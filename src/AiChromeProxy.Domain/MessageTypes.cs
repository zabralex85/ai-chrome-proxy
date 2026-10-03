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

	/// <summary>Server → client: files changed in the mirror (<c>Sync.SyncRemotePayload</c>).</summary>
	public const string SyncRemote = "sync.remote";

	/// <summary>Client → server: asks for a mirror file (<c>Sync.SyncFetchPayload</c>); reply <see cref="SyncData"/>.</summary>
	public const string SyncFetch = "sync.fetch";

	/// <summary>Server → client: part of a mirror file (<c>Sync.SyncDataPayload</c>).</summary>
	public const string SyncData = "sync.data";

	/// <summary>Client → server: a remote change is applied (<c>Sync.SyncAckPayload</c>); echoed back.</summary>
	public const string SyncAck = "sync.ack";

	/// <summary>Client → server: asks for the project settings (<c>Sync.ProjectSettingsPayload</c>); reply <see cref="ProjectSettings"/>.</summary>
	public const string ProjectSettingsGet = "project.settings.get";

	/// <summary>Client → server: saves the project settings (<c>Sync.ProjectSettingsPayload</c>); reply <see cref="ProjectSettings"/>.</summary>
	public const string ProjectSettingsSet = "project.settings.set";

	/// <summary>Server → client: the project settings (<c>Sync.ProjectSettingsPayload</c>).</summary>
	public const string ProjectSettings = "project.settings";
}
