using System.Reflection;
using System.Text.Json;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Domain;

public sealed class ProductVersionTests
{
	[Fact]
	public void Of_InformationalVersionWithoutBuildMetadata()
	{
		// The release build stamps -p:Version (e.g. 0.3.2), local builds 1.0.0: compare with the attribute, not a literal.
		var assembly = typeof(ProductVersionTests).Assembly;
		var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

		var version = ProductVersion.Of(assembly);

		Assert.Equal(informational.Split('+')[0], version);
		Assert.DoesNotContain('+', version);
		Assert.NotEmpty(version);
	}

	[Theory]
	[InlineData("""{"serverVersion":"0.3.1"}""", "0.3.0", true)]
	[InlineData("""{"serverVersion":"0.3.0"}""", "0.3.1", true)]
	[InlineData("""{"serverVersion":"0.3.1"}""", "0.3.1", false)]
	[InlineData("""{"serverTime":"2026-10-03T00:00:00Z"}""", "0.3.1", false)]
	[InlineData("""{"serverVersion":""}""", "0.3.1", false)]
	[InlineData("""{"serverVersion":null}""", "0.3.1", false)]
	[InlineData("""{"serverVersion":3}""", "0.3.1", false)]
	[InlineData("[]", "0.3.1", false)]
	public void ServerDiffers_OnlyWhenThePongNamesAnotherVersion(string pong, string client, bool expected)
	{
		Assert.Equal(expected, ProductVersion.ServerDiffers(JsonDocument.Parse(pong).RootElement, client));
	}
}
