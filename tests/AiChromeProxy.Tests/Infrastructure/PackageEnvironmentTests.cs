using AiChromeProxy.Infrastructure.Chat;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class PackageEnvironmentTests
{
	private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

	[Fact]
	public void NoFolder_ReturnsTheConfiguredInstance()
	{
		var configured = new Dictionary<string, string> { ["A"] = "1" };

		Assert.Same(configured, PackageEnvironment.With(configured, "C:\b", null));
	}

	[Fact]
	public void FolderOnly_AddsTheFourVariables()
	{
		var env = PackageEnvironment.With(None, null, "C:\n");

		Assert.Equal("C:\n", env["NUGET_FALLBACK_PACKAGES"]);
		Assert.Equal("true", env["RestoreIgnoreFailedSources"]);
		Assert.Equal("false", env["NuGetAudit"]);
		Assert.Equal("NU1801", env["WarningsNotAsErrors"]);
		Assert.Equal(4, env.Count);
	}

	[Fact]
	public void ConfiguredFallback_IsExtended()
	{
		var env = PackageEnvironment.With(new Dictionary<string, string> { ["NUGET_FALLBACK_PACKAGES"] = "C:\a;" }, "C:\b", "C:\n");

		Assert.Equal("C:\a;C:\n", env["NUGET_FALLBACK_PACKAGES"]);
	}

	[Fact]
	public void InheritedFallback_IsUsedWhenNotConfigured()
	{
		Assert.Equal("C:\b;C:\n", PackageEnvironment.With(None, "C:\b", "C:\n")["NUGET_FALLBACK_PACKAGES"]);
	}

	[Fact]
	public void ConfiguredValues_WinOverTheDefaults_IgnoringCase()
	{
		var env = PackageEnvironment.With(
			new Dictionary<string, string> { ["restoreignorefailedsources"] = "false", ["WarningsNotAsErrors"] = "CS1234" },
			null,
			"C:\n");

		Assert.Equal("false", env["RestoreIgnoreFailedSources"]);
		Assert.Equal("CS1234", env["WarningsNotAsErrors"]);
		Assert.Single(env, p => string.Equals(p.Key, "RestoreIgnoreFailedSources", StringComparison.OrdinalIgnoreCase));
	}
}
