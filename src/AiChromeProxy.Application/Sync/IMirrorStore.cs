namespace AiChromeProxy.Application.Sync;

/// <summary>
/// The server's copy of a synced folder: <c>&lt;root&gt;\&lt;repo&gt;</c>. Paths are protocol paths (relative, <c>/</c>-separated);
/// an invalid path, one resolving outside the repo folder or one crossing a link or junction is refused with <see cref="Transport.EnvelopeException"/>.
/// The link checks protect against links that already exist in the mirror; a process of the same account racing to swap one in
/// between check and use (TOCTOU) is outside the threat model.
/// </summary>
public interface IMirrorStore
{
	/// <summary>
	/// SHA-256 (lower-case hex) of the mirror file; null when there is no regular file. Read while other processes may write it;
	/// cached per path by size and write time when the file did not change during the read.
	/// </summary>
	Task<string?> GetHashAsync(string repo, string path, CancellationToken ct);

	/// <summary>Paths of the regular files in the repo folder; links and junctions (and what is behind them) are skipped.</summary>
	IReadOnlyList<string> ListFiles(string repo);

	/// <summary>
	/// Creates the temporary file <c>&lt;path&gt;.&lt;tag&gt;.aicp-tmp</c> the upload is assembled in (<paramref name="tag"/>: the session's,
	/// <c>SyncPath.TempTagLength</c> characters, so that sessions never share a temporary file), creating parent folders; an existing entry at that
	/// name (a leftover, or a link) is removed first, never written through.
	/// </summary>
	Stream CreateTemp(string repo, string path, string tag);

	/// <summary>Replaces the mirror file with its completed temporary file.</summary>
	void Commit(string repo, string path, string tag);

	/// <summary>Deletes the temporary file, if any.</summary>
	void DiscardTemp(string repo, string path, string tag);

	/// <summary>
	/// Deletes leftover <c>.aicp-tmp</c> files in the repo folder (links and junctions are not entered), except the temporary file of
	/// <paramref name="keep"/> with <paramref name="tag"/> and those still open by another upload.
	/// </summary>
	void DeleteStaleTemps(string repo, string? keep, string tag);

	/// <summary>Deletes a regular file (never a link's target) and the folders this leaves empty, up to the repo folder.</summary>
	void Delete(string repo, string path);
}
