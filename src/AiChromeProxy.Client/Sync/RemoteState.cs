using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Sync;

public enum RemoteStatus
{
	/// <summary>Not written yet: no write access, automatic apply is off, or the next cycle writes it.</summary>
	Waiting,

	/// <summary>Changed here and on the server since they last agreed: neither uploaded nor written until <b>Keep mine</b> or <b>Take server's</b>.</summary>
	Conflict,
}

/// <summary>A server change the engine has not applied yet (<see cref="SyncEngine.Remote"/>).</summary>
/// <param name="Change">The server's latest change of the path.</param>
/// <param name="Status">Waiting or in conflict.</param>
/// <param name="Local">The client's hash when the conflict was found (null: deleted here).</param>
public sealed record RemoteItem(RemoteChange Change, RemoteStatus Status, string? Local);
