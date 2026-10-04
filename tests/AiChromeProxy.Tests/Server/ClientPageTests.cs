using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Server.Hosting;

namespace AiChromeProxy.Tests.Server;

public sealed class ClientPageTests
{
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

	private static string Hash(string script) => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";
}
