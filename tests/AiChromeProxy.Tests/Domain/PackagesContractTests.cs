using System.Text.Json;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;

namespace AiChromeProxy.Tests.Domain;

public sealed class PackagesContractTests
{
	[Fact]
	public void Payloads_AreCamelCase()
	{
		Assert.Equal("""{"packages":[{"id":"corp.a","version":"1.2.3","contentHash":"h"}]}""", Json(new NuGetHavePayload([new NuGetPackageInfo("corp.a", "1.2.3", "h")])));
		Assert.Equal("""{"missing":[{"id":"corp.a","version":"1.2.3"}]}""", Json(new NuGetMissingPayload([new NuGetPackageKey("corp.a", "1.2.3")])));
		Assert.Equal("""{"id":"corp.a","version":"1.2.3","contentHash":"h","fileCount":2,"totalSize":3}""", Json(new NuGetBeginPayload("corp.a", "1.2.3", "h", 2, 3)));
		Assert.Equal("""{"uploadId":"u"}""", Json(new NuGetBegunPayload("u")));
		Assert.Equal("""{"uploadId":"u","path":"lib/a.dll","offset":0,"data":"AAEC","last":true,"size":3}""", Json(new NuGetChunkPayload("u", "lib/a.dll", 0, "AAEC", true, 3)));
		Assert.Equal("""{"uploadId":"u","path":"lib/a.dll","offset":3,"data":"AAEC","last":false}""", Json(new NuGetChunkPayload("u", "lib/a.dll", 3, "AAEC", false)));
		Assert.Equal("""{"id":"corp.a","version":"1.2.3"}""", Json(new NuGetStoredPayload("corp.a", "1.2.3")));
	}

	[Fact]
	public void Limits_And_Types()
	{
		Assert.Equal(250L * 1024 * 1024, PackageLimits.MaxPackageBytes);
		Assert.Equal(2_000, PackageLimits.MaxPackageFiles);
		Assert.Equal(2_000, PackageLimits.MaxHavePackages);
		Assert.Equal(16 * 1024, PackageLimits.ChunkSize);
		Assert.Equal("packages.nuget.have", MessageTypes.PackagesNuGetHave);
		Assert.Equal("packages.nuget.missing", MessageTypes.PackagesNuGetMissing);
		Assert.Equal("packages.nuget.begin", MessageTypes.PackagesNuGetBegin);
		Assert.Equal("packages.nuget.begun", MessageTypes.PackagesNuGetBegun);
		Assert.Equal("packages.nuget.chunk", MessageTypes.PackagesNuGetChunk);
		Assert.Equal("packages.nuget.stored", MessageTypes.PackagesNuGetStored);
	}

	[Theory]
	[InlineData("Corp.Private", true)]
	[InlineData("corp_x-1", true)]
	[InlineData("", false)]
	[InlineData(null, false)]
	[InlineData("..", false)]
	[InlineData(".hidden", false)]
	[InlineData("a/b", false)]
	[InlineData("a b", false)]
	[InlineData("corp.a.", false)]
	public void Ids(string? id, bool valid) => Assert.Equal(valid, NuGetNames.IsValidId(id));

	[Fact]
	public void Ids_LongerThan100_AreInvalid() => Assert.False(NuGetNames.IsValidId(new string('a', 101)));

	[Theory]
	[InlineData("1.2.3", true)]
	[InlineData("1.0.0-beta.2+build.7", true)]
	[InlineData("", false)]
	[InlineData("1..2", false)]
	[InlineData("../1", false)]
	[InlineData("1.0 ", false)]
	[InlineData("1.0.", false)]
	public void Versions(string version, bool valid) => Assert.Equal(valid, NuGetNames.IsValidVersion(version));

	[Fact]
	public void ContentHash_IsBase64OfSixtyFourBytes()
	{
		Assert.True(NuGetNames.IsValidContentHash(Convert.ToBase64String(new byte[64])));
		Assert.False(NuGetNames.IsValidContentHash(Convert.ToBase64String(new byte[32])));
		Assert.False(NuGetNames.IsValidContentHash("not base64!"));
		Assert.False(NuGetNames.IsValidContentHash(null));
	}

	[Theory]
	[InlineData("https://api.nuget.org/v3/index.json", true)]
	[InlineData("https://API.NUGET.ORG/v3/index.json", true)]
	[InlineData("https://artifactory.corp.example/api/nuget/v3/corp/index.json", false)]
	[InlineData("C:\\feeds\\local", false)]
	[InlineData(null, false)]
	public void PublicSource(string? source, bool expected) => Assert.Equal(expected, NuGetNames.IsPublicSource(source));

	private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
