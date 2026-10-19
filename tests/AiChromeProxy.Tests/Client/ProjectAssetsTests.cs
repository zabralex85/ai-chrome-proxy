using System.Security.Cryptography;
using AiChromeProxy.Client.Packages;

namespace AiChromeProxy.Tests.Client;

/// <summary><c>project.assets.json</c> and <c>.nupkg.metadata</c> parsing.</summary>
public sealed class ProjectAssetsTests
{
	private static readonly string Hash = Convert.ToBase64String(SHA512.HashData("package"u8));

	[Fact]
	public void Parse_ReturnsThePackages_LowerCased_IgnoringProjectReferences()
	{
		const string json = """
			{
			  "version": 3,
			  "targets": { "net10.0": { "Corp.A/1.2.3": { "type": "package" } } },
			  "libraries": {
			    "Corp.A/1.2.3": { "sha512": "x", "type": "package", "path": "corp.a/1.2.3", "files": [ "corp.a.1.2.3.nupkg" ] },
			    "Newtonsoft.Json/13.0.3-Beta1": { "type": "package", "path": "newtonsoft.json/13.0.3-beta1" },
			    "Lib/1.0.0": { "type": "project", "path": "../Lib/Lib.csproj", "msbuildProject": "../Lib/Lib.csproj" }
			  }
			}
			""";

		Assert.Equal([new PackageRef("corp.a", "1.2.3"), new PackageRef("newtonsoft.json", "13.0.3-beta1")], ProjectAssets.Parse(json));
	}

	[Fact]
	public void Parse_DuplicateKeysAcrossCasing_AreOne()
	{
		const string json = """{"libraries":{"Corp.A/1.0.0":{"type":"package"},"corp.a/1.0.0":{"type":"package"}}}""";

		Assert.Equal([new PackageRef("corp.a", "1.0.0")], ProjectAssets.Parse(json));
	}

	[Fact]
	public void Parse_WithoutLibraries_IsEmpty() => Assert.Empty(ProjectAssets.Parse("""{"version":3}"""));

	[Fact]
	public void Parse_SkipsKeysThatAreNotPackageNames()
	{
		const string json = """{"libraries":{"NoVersion":{"type":"package"},"../evil/1.0":{"type":"package"},"Ok/1.0":{"type":"package"}}}""";

		Assert.Equal([new PackageRef("ok", "1.0")], ProjectAssets.Parse(json));
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[1,2]")]
	[InlineData("""{"libraries":[]}""")]
	public void Parse_NotAnAssetsFile_Throws(string json) => Assert.Throws<FormatException>(() => ProjectAssets.Parse(json));

	[Fact]
	public void Metadata_RealFormat_HashAndSource()
	{
		var json = $$"""{"version":2,"contentHash":"{{Hash}}","source":"https://corp.example/nuget/v3/index.json"}""";

		Assert.Equal((Hash, "https://corp.example/nuget/v3/index.json"), NuGetMetadata.Parse(json));
	}

	[Fact]
	public void Metadata_WithoutSource_HasANullSource() => Assert.Equal((Hash, (string?)null), NuGetMetadata.Parse($$"""{"version":2,"contentHash":"{{Hash}}"}"""));

	[Theory]
	[InlineData("garbage")]
	[InlineData("[]")]
	[InlineData("""{"version":2}""")]
	[InlineData("""{"version":2,"contentHash":"short"}""")]
	[InlineData("""{"version":2,"contentHash":1}""")]
	public void Metadata_Garbage_IsNull(string json) => Assert.Null(NuGetMetadata.Parse(json));
}
