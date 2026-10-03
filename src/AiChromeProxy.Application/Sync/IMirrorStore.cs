namespace AiChromeProxy.Application.Sync;

/// <summary>
/// The server's copy of a synced folder: <c>&lt;root&gt;\&lt;repo&gt;</c>. Paths are protocol paths (relative, <c>/</c>-separated);
/// an invalid path, one resolving outside the repo folder or one crossing a link or junction is refused with <see cref="Transport.EnvelopeException"/>.
/// </summary>
public interface IMirrorStore
{
	/// <summary>SHA-256 (lower-case hex) of the mirror file; null when there is no regular file. Cached per path by size and write time.</summary>
	Task<string?> GetHashAsync(string repo, string path, CancellationToken ct);

	/// <summary>Paths of the regular files in the repo folder; links and junctions (and what is behind them) are skipped.</summary>
	IReadOnlyList<string> ListFiles(string repo);

	/// <summary>Creates (or truncates) the temporary file <c>&lt;path&gt;.aicp-tmp</c> the upload is assembled in, creating parent folders.</summary>
	Stream CreateTemp(string repo, string path);

	/// <summary>Replaces the mirror file with its completed temporary file.</summary>
	void Commit(string repo, string path);

	/// <summary>Deletes the temporary file, if any.</summary>
	void DiscardTemp(string repo, string path);

	/// <summary>Deletes a regular file (never a link's target) and the folders this leaves empty, up to the repo folder.</summary>
	void Delete(string repo, string path);
}
