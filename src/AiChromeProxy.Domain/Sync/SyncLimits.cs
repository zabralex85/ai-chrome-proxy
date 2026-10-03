namespace AiChromeProxy.Domain.Sync;

/// <summary>Bounds shared by the browser and the server; every message stays under SignalR's 32 KB receive limit.</summary>
public static class SyncLimits
{
	/// <summary>Larger files are not synced (shown as "too large").</summary>
	public const long MaxFileSize = 20L * 1024 * 1024;

	/// <summary>More files to sync than this (after excludes) is refused.</summary>
	public const int MaxFiles = 20_000;

	/// <summary>Raw bytes per <c>sync.chunk</c> (about 21.4 KB as base64).</summary>
	public const int ChunkSize = 16 * 1024;

	/// <summary>Entries per <c>sync.manifest</c> / <c>sync.delta</c> page.</summary>
	public const int MaxPageEntries = 500;

	/// <summary>Serialized entries per page; leaves room for the envelope under the 32 KB limit.</summary>
	public const int MaxPageBytes = 24_000;
}
