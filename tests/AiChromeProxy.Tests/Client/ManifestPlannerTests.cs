using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class ManifestPlannerTests
{
	/// <summary>SignalR's default <c>MaximumReceiveMessageSize</c>.</summary>
	internal const int SignalRLimit = 32 * 1024;

	private static readonly string Hash = new('a', 64);

	[Fact]
	public void Pages_SplitByCount()
	{
		var pages = SyncPages.Split(Enumerable.Range(0, 1200), _ => 1).ToList();

		Assert.Equal([500, 500, 200], pages.Select(p => p.Count));
	}

	[Fact]
	public void Pages_SplitByBytes_OversizedItemAlone()
	{
		var pages = SyncPages.Split([10_000, 10_000, 10_000, 30_000, 1], i => i).ToList();

		Assert.Equal([2, 1, 1, 1], pages.Select(p => p.Count));
	}

	[Fact]
	public void Pages_Empty_None()
	{
		Assert.Empty(SyncPages.Split(Array.Empty<int>(), _ => 1));
	}

	[Fact]
	public void ManifestPages_Empty_OneFinalPage()
	{
		var page = Assert.Single(ManifestPlanner.ManifestPages("repo", []));

		Assert.True(page.Final);
		Assert.Empty(page.Entries);
		Assert.Equal("repo", page.Repo);
	}

	[Fact]
	public void ManifestPages_OnlyLastIsFinal_AllEntriesInOrder()
	{
		var entries = Enumerable.Range(0, 1000).Select(i => new ManifestEntry($"src/file{i:D4}.cs", i, Hash)).ToList();

		var pages = ManifestPlanner.ManifestPages("repo", entries);

		Assert.True(pages.Count > 1);
		Assert.All(pages[..^1], p => Assert.False(p.Final));
		Assert.True(pages[^1].Final);
		Assert.Equal(entries, pages.SelectMany(p => p.Entries));
	}

	[Fact]
	public void ManifestPages_WorstCasePathsAndKeep_EveryEnvelopeUnderSignalRLimit()
	{
		// 260 non-ASCII chars: each is escaped to \uXXXX (6 bytes) in JSON.
		var entries = Enumerable.Range(0, 300).Select(i => new ManifestEntry($"{i:D3}/" + new string('ж', 256), long.MaxValue, Hash)).ToList();
		var keep = Enumerable.Range(0, 1200).Select(i => $"{i:D4}/" + new string('ж', 254) + (i % 2 == 0 ? "/" : "ж")).ToList();

		var pages = ManifestPlanner.ManifestPages(new string('r', RepoName.MaxLength), entries, keep);

		Assert.All(pages, p => Assert.True(WireSize(Envelope.Create(MessageTypes.SyncManifest, p, Guid.NewGuid().ToString("N"))) < SignalRLimit - 4096));
		Assert.All(pages, p => Assert.True(p.Entries.Count + (p.Keep?.Count ?? 0) <= SyncLimits.MaxPageEntries));
		Assert.Equal(300, pages.Sum(p => p.Entries.Count));
		Assert.Equal(keep, pages.SelectMany(p => p.Keep ?? []));
	}

	[Fact]
	public void ManifestPages_ManySmallKeep_PagedByCount()
	{
		var keep = Enumerable.Range(0, 1200).Select(i => $"k{i}").ToList();

		var pages = ManifestPlanner.ManifestPages("repo", [new("a.txt", 1, Hash)], keep);

		Assert.Equal([1, 500, 500, 200], pages.Select(p => p.Entries.Count + (p.Keep?.Count ?? 0)));
		Assert.Equal([false, false, false, true], pages.Select(p => p.Final));
	}

	[Fact]
	public void ManifestPages_KeepAfterEntries_OnlyLastIsFinal()
	{
		var pages = ManifestPlanner.ManifestPages("repo", [new("a.txt", 1, Hash)], ["big.bin", "locked/"]);

		Assert.Equal(2, pages.Count);
		Assert.Equal(["a.txt"], pages[0].Entries.Select(e => e.Path));
		Assert.Null(pages[0].Keep);
		Assert.False(pages[0].Final);
		Assert.Empty(pages[1].Entries);
		Assert.Equal(["big.bin", "locked/"], pages[1].Keep);
		Assert.True(pages[1].Final);
	}

	[Fact]
	public void ManifestPages_OnlyKeep_OneFinalPage()
	{
		var page = Assert.Single(ManifestPlanner.ManifestPages("repo", [], ["locked/"]));

		Assert.True(page.Final);
		Assert.Equal(["locked/"], page.Keep);
	}

	[Fact]
	public void DeltaPages_UpsertsAddedAndChanged_DeletesRemoved()
	{
		var known = new Dictionary<string, ManifestEntry>
		{
			["same.txt"] = new("same.txt", 1, Hash),
			["changed.txt"] = new("changed.txt", 1, Hash),
			["gone.txt"] = new("gone.txt", 1, Hash),
		};
		ManifestEntry[] current = [new("same.txt", 1, Hash), new("changed.txt", 2, new string('b', 64)), new("new.txt", 3, Hash)];

		var pages = ManifestPlanner.DeltaPages("repo", known, current);

		Assert.Equal(2, pages.Count);
		Assert.Equal([current[1], current[2]], pages[0].Upserts);
		Assert.Empty(pages[0].Deletes);
		Assert.Empty(pages[1].Upserts);
		Assert.Equal(["gone.txt"], pages[1].Deletes);
	}

	[Fact]
	public void DeltaPages_WorstCasePaths_EveryEnvelopeUnderSignalRLimit()
	{
		static string WorstPath(string prefix, int i) => $"{prefix}{i:D3}/" + new string('ж', 255);

		var known = Enumerable.Range(0, 300).Select(i => new ManifestEntry(WorstPath("o", i), long.MaxValue, Hash)).ToDictionary(e => e.Path);
		var current = Enumerable.Range(0, 300).Select(i => new ManifestEntry(WorstPath("n", i), long.MaxValue, Hash)).ToList();

		var pages = ManifestPlanner.DeltaPages(new string('r', RepoName.MaxLength), known, current);

		Assert.All(pages, p => Assert.True(WireSize(Envelope.Create(MessageTypes.SyncDelta, p, Guid.NewGuid().ToString("N"))) < SignalRLimit - 4096));
		Assert.Equal(300, pages.Sum(p => p.Upserts.Count));
		Assert.Equal(300, pages.Sum(p => p.Deletes.Count));
	}

	[Fact]
	public void DeltaPages_NothingChanged_Empty()
	{
		var known = new Dictionary<string, ManifestEntry> { ["a"] = new("a", 1, Hash) };

		Assert.Empty(ManifestPlanner.DeltaPages("repo", known, [new("a", 1, Hash)]));
	}

	[Fact]
	public void DeltaPages_CaseOnlyRename_UpsertWithoutDelete()
	{
		var known = new Dictionary<string, ManifestEntry> { ["readme.md"] = new("readme.md", 1, Hash) };

		var page = Assert.Single(ManifestPlanner.DeltaPages("repo", known, [new("README.md", 1, Hash)]));

		Assert.Equal("README.md", Assert.Single(page.Upserts).Path);
		Assert.Empty(page.Deletes);
	}

	internal static int WireSize(Envelope envelope) =>
		JsonSerializer.SerializeToUtf8Bytes(new { type = 1, target = "Send", arguments = new[] { envelope } }, JsonSerializerOptions.Web).Length;
}
