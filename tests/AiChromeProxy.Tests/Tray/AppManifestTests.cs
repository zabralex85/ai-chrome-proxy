using System.Xml.Linq;

namespace AiChromeProxy.Tests.Tray;

public sealed class AppManifestTests
{
	private static readonly XNamespace AsmV1 = "urn:schemas-microsoft-com:asm.v1";
	private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";

	/// <summary>Windows parses the embedded manifest strictly (e.g. "--" inside a comment): a bad one stops the tray from starting at all.</summary>
	[Fact]
	public void Manifest_IsStrictXml_AsInvoker()
	{
		var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Tray", "app.manifest"));

		Assert.Equal(AsmV1 + "assembly", manifest.Root!.Name);
		Assert.Equal("asInvoker", (string?)manifest.Descendants(AsmV3 + "requestedExecutionLevel").Single().Attribute("level"));
	}
}
