namespace AiChromeProxy.Application.Sync;

public enum SyncAction
{
	/// <summary>Both sides have the same content (or neither has the file): the base becomes it.</summary>
	InSync,

	/// <summary>Only the client changed: request its upload.</summary>
	Upload,

	/// <summary>The client deleted the file and the mirror still has the agreed version: delete the mirror file.</summary>
	DeleteMirror,

	/// <summary>The mirror changed (alone or together with the client): tell the client (sync.remote).</summary>
	Push,
}
