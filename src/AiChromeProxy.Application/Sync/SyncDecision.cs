namespace AiChromeProxy.Application.Sync;

/// <summary>Decides one path from the client's, the mirror's and the agreed hash.</summary>
public static class SyncDecision
{
	/// <summary>Determines the sync action based on the client, mirror, and base hashes.</summary>
	/// <param name="client">The client's SHA-256, null when the client has no file.</param>
	/// <param name="mirror">The mirror's SHA-256, null when the mirror has no file.</param>
	/// <param name="baseHash">The last agreed SHA-256, null when there is none.</param>
	/// <param name="baselined">Whether the repo has had its first full manifest since bases are kept.</param>
	/// <returns>The action to take for this file.</returns>
	public static SyncAction Decide(string? client, string? mirror, string? baseHash, bool baselined)
	{
		if (client == mirror)
		{
			return SyncAction.InSync;
		}

		if (!baselined || mirror == baseHash)
		{
			return client is null ? SyncAction.DeleteMirror : SyncAction.Upload;
		}

		return SyncAction.Push;
	}
}
