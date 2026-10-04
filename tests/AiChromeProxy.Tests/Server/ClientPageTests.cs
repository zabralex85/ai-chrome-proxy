using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Server.Hosting;
using Microsoft.AspNetCore.Components;

namespace AiChromeProxy.Tests.Server;

public sealed class ClientPageTests
{
	[Fact]
	public void Render_ImportMapLeavesTheVendoredMonacoOut_ButKeepsTheModules()
	{
		static ResourceAsset Asset(string url, string label) => new(url, [new ResourceAssetProperty("label", label)]);
		var assets = new ResourceAssetCollection(
		[
			new ResourceAsset("_framework/blazor.webassembly.js", [new ResourceAssetProperty("label", "_framework/blazor.webassembly.js")]),
			new ResourceAsset("css/app.css", [new ResourceAssetProperty("label", "css/app.css")]),
			Asset("lib/monaco/vs/loader.fp1.js", "lib/monaco/vs/loader.js"),
			Asset("lib/monaco/vs/editor/editor.main.fp2.js", "lib/monaco/vs/editor/editor.main.js"),
			Asset("js/viewer.fp3.js", "js/viewer.js"),
		]);

		var html = ClientPage.Render("<script type=\"importmap\">{{importmap}}</script>", assets);

		Assert.DoesNotContain("lib/monaco", html, StringComparison.Ordinal);
		Assert.Contains("js/viewer.fp3.js", html, StringComparison.Ordinal);
	}

	[Fact]
	public void ContentSecurityPolicy_AllowsOnlyTheInlineScriptsOfThePageByHash()
	{
		const string importMap = "{\"imports\":{\"./a.js\":\"./a.abc.js\"}}";
		var html = $"<script type=\"importmap\">{importMap}</script><script src=\"x.js\"></script><script>run()</script>";

		var policy = ClientPage.ContentSecurityPolicy(html);

		Assert.Contains(Hash(importMap), policy, StringComparison.Ordinal);
		Assert.Contains(Hash("run()"), policy, StringComparison.Ordinal);
		Assert.DoesNotContain(Hash(string.Empty), policy, StringComparison.Ordinal);
	}

	[Fact]
	public void ContentSecurityPolicy_HashesTheScriptAsTheBrowserSeesIt_LineBreaksAreLf()
	{
		var policy = ClientPage.ContentSecurityPolicy("<script>a\r\nb</script>");

		Assert.Contains(Hash("a\nb"), policy, StringComparison.Ordinal);
		Assert.DoesNotContain(Hash("a\r\nb"), policy, StringComparison.Ordinal);
	}

	[Fact]
	public void ContentSecurityPolicy_HasNoUnsafeInlineScriptAndLocksTheRest()
	{
		var directives = ClientPage.ContentSecurityPolicy("<script type=\"importmap\">{}</script>").Split(';', StringSplitOptions.TrimEntries).ToDictionary(d => d.Split(' ')[0], d => d);

		Assert.DoesNotContain("'unsafe-inline'", directives["script-src"], StringComparison.Ordinal);
		Assert.DoesNotContain("'unsafe-eval'", directives["script-src"], StringComparison.Ordinal);
		Assert.Contains("'wasm-unsafe-eval'", directives["script-src"], StringComparison.Ordinal);
		Assert.Equal("default-src 'self'", directives["default-src"]);
		Assert.Equal("object-src 'none'", directives["object-src"]);
		Assert.Equal("base-uri 'self'", directives["base-uri"]);
		Assert.Equal("frame-ancestors 'none'", directives["frame-ancestors"]);
		Assert.Equal("connect-src 'self'", directives["connect-src"]);
	}

	[Fact]
	public void ContentSecurityPolicy_LetsTheCodeViewerRun_ItsWorkerFromABlobAndItsIconFontFromData()
	{
		var directives = ClientPage.ContentSecurityPolicy(string.Empty).Split(';', StringSplitOptions.TrimEntries).ToDictionary(d => d.Split(' ')[0], d => d);

		Assert.Equal("worker-src 'self' blob:", directives["worker-src"]);
		Assert.Equal("font-src 'self' data:", directives["font-src"]);
		Assert.Equal("style-src 'self' 'unsafe-inline'", directives["style-src"]);
	}

	private static string Hash(string script) => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";
}
