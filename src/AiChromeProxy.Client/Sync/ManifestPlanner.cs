using System.Text.Json;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Sync;

/// <summary>Turns a scan into protocol pages: the full manifest, or the delta against what the server already has.</summary>
public static class ManifestPlanner
{
	/// <summary>The full manifest in pages (entries, then the keep list); always at least one page, the last one <c>final</c>.</summary>
	public static List<SyncManifestPayload> ManifestPages(string repo, IReadOnlyList<ManifestEntry> entries, IReadOnlyList<string>? keep = null)
	{
		List<SyncManifestPayload> pages =
		[
			.. SyncPages.Split(entries, EntrySize).Select(p => new SyncManifestPayload(repo, p, Final: false)),
			.. SyncPages.Split(keep ?? [], PathSize).Select(p => new SyncManifestPayload(repo, [], Final: false, p)),
		];
		if (pages.Count == 0)
		{
			pages.Add(new SyncManifestPayload(repo, [], Final: true));
		}
		else
		{
			pages[^1] = pages[^1] with { Final = true };
		}

		return pages;
	}

	/// <summary>
	/// Delta pages (upserts first, then deletes) from what the server has (<paramref name="known"/>) to <paramref name="current"/>; empty when nothing changed.
	/// A path that only changed case is not deleted: the mirror is case-insensitive, so deleting it would delete the upserted file.
	/// </summary>
	public static List<SyncDeltaPayload> DeltaPages(string repo, IReadOnlyDictionary<string, ManifestEntry> known, IReadOnlyList<ManifestEntry> current)
	{
		var upserts = current.Where(e => !known.TryGetValue(e.Path, out var k) || k != e).ToList();
		var currentPaths = current.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var deletes = known.Keys.Where(p => !currentPaths.Contains(p)).Order(StringComparer.Ordinal).ToList();
		return
		[
			.. SyncPages.Split(upserts, EntrySize).Select(p => new SyncDeltaPayload(repo, p, [])),
			.. SyncPages.Split(deletes, PathSize).Select(p => new SyncDeltaPayload(repo, [], p)),
		];
	}

	/// <summary>Exact serialized size plus the separating comma (non-ASCII is escaped as \uXXXX, so it counts up to 6 bytes per char).</summary>
	private static int EntrySize(ManifestEntry entry) => JsonSerializer.SerializeToUtf8Bytes(entry, JsonSerializerOptions.Web).Length + 1;

	private static int PathSize(string path) => JsonSerializer.SerializeToUtf8Bytes(path, JsonSerializerOptions.Web).Length + 1;
}
