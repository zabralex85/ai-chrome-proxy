using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class ManifestPlannerTests
{
	/// <summary>SignalR's default <c>MaximumReceiveMessageSize</c>.</summary>
	private const int SignalRLimit = 32 * 1024;

	private static readonly string Hash = new('a', 64);

	[Fact]
	public void Pages_SplitByCount()
	{
		var pages = ManifestPlanner.Pages(Enumerable.Range(0, 1200), _ => 1).ToList();

		Assert.Equal([500, 500, 200], pages.Select(p => p.Count));
	}

	[Fact]
	public void Pages_SplitByBytes_OversizedItemAlone()
	{
		var pages = ManifestPlanner.Pages([10_000, 10_000, 10_000, 30_000, 1], i => i).ToList();

		Assert.Equal([2, 1, 1, 1], pages.Select(p => p.Count));
	}

	[Fact]
	public void Pages_Empty_None()
	{
		Assert.Empty(ManifestPlanner.Pages(Array.Empty<int>(), _ => 1));
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
	public void ManifestPages_WorstCasePaths_EveryEnvelopeUnderSignalRLimit()
	{
		// 260 non-ASCII chars: each is escaped to \uXXXX (6 bytes) in JSON.
		var entries = Enumerable.Range(0, 300).Select(i => new ManifestEntry($"{i:D3}/" + new string('ж', 256), long.MaxValue, Hash)).ToList();

		var pages = ManifestPlanner.ManifestPages(new string('r', RepoName.MaxLength), entries);

		Assert.All(pages, p => Assert.True(WireSize(Envelope.Create(MessageTypes.SyncManifest, p, Guid.NewGuid().ToString("N"))) < SignalRLimit));
		Assert.Equal(300, pages.Sum(p => p.Entries.Count));
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

	private static int WireSize(Envelope envelope) =>
		JsonSerializer.SerializeToUtf8Bytes(new { type = 1, target = "Send", arguments = new[] { envelope } }, JsonSerializerOptions.Web).Length;
}
