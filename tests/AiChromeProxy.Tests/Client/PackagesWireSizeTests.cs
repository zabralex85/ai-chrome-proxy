using AiChromeProxy.Client.Packages;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

/// <summary>The largest <c>packages.nuget.*</c> messages the browser sends stay under SignalR's 32 KB receive limit (see <see cref="ManifestPlannerTests"/>).</summary>
public sealed class PackagesWireSizeTests
{
	private const int Limit = ManifestPlannerTests.SignalRLimit - 4096;

	/// <summary>88 plus signs, each escaped by JSON to six bytes (backslash, u, four hex digits): the worst a base64 sha512 can be.</summary>
	private static readonly string WorstHash = new string('+', 86) + "==";

	private static readonly string WorstId = new('a', NuGetNames.MaxIdLength);

	private static readonly string WorstVersion = new('1', NuGetNames.MaxVersionLength);

	[Fact]
	public void HavePages_OfTheMostPackagesWithTheLongestNames_AreEachUnderTheLimit()
	{
		var packages = Enumerable.Range(0, PackageLimits.MaxHavePackages)
			.Select(i => new NuGetPackageInfo($"{i:D4}{WorstId[4..]}", WorstVersion, WorstHash))
			.ToList();

		var pages = PackagesEngine.HavePages(packages).ToList();

		Assert.All(pages, p => Assert.True(Size(MessageTypes.PackagesNuGetHave, p) < Limit));
		Assert.All(pages, p => Assert.InRange(p.Packages.Count, 1, PackageLimits.MaxHavePackages));
		Assert.Equal(packages, pages.SelectMany(p => p.Packages));
	}

	[Fact]
	public void HavePages_ManySmallPackages_StayWithinTheCountLimit()
	{
		var packages = Enumerable.Range(0, 2 * PackageLimits.MaxHavePackages).Select(i => new NuGetPackageInfo($"p{i}", "1", "h")).ToList();

		var pages = PackagesEngine.HavePages(packages).ToList();

		Assert.All(pages, p => Assert.True(p.Packages.Count <= Math.Min(SyncLimits.MaxPageEntries, PackageLimits.MaxHavePackages)));
		Assert.Equal(packages, pages.SelectMany(p => p.Packages));
	}

	[Fact]
	public void Begin_IsUnderTheLimit()
	{
		var begin = new NuGetBeginPayload(WorstId, WorstVersion, WorstHash, int.MaxValue, long.MaxValue);

		Assert.True(Size(MessageTypes.PackagesNuGetBegin, begin) < Limit);
	}

	[Fact]
	public void Chunk_WithTheMostData_TheLongestPath_AndASize_IsUnderTheLimit()
	{
		// 260 non-ASCII chars (the sync path limit), each escaped to \uXXXX (6 bytes) in JSON.
		var path = new string('ж', SyncPath.MaxSegmentLength) + "/" + new string('ж', SyncPath.MaxLength - SyncPath.MaxSegmentLength - 1);
		var data = SyncData.Encode(new byte[PackageLimits.ChunkSize]);
		var chunk = new NuGetChunkPayload(Guid.NewGuid().ToString("N"), path, long.MaxValue, data, true, long.MaxValue);

		Assert.Equal(SyncPath.MaxLength, path.Length);
		Assert.True(Size(MessageTypes.PackagesNuGetChunk, chunk) < Limit);
	}

	private static int Size<T>(string type, T payload) => ManifestPlannerTests.WireSize(Envelope.Create(type, payload, Guid.NewGuid().ToString("N")));
}
