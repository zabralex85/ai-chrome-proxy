using System.Text.RegularExpressions;
using AiChromeProxy.Client.Chat;

namespace AiChromeProxy.Tests.Client;

/// <summary>Claude's text is untrusted (the patterns look inside real tags: escaped text such as "&lt;div id=x&gt;" is harmless) (prompt injection through files it reads): whatever it writes, the markup has no script, handler, style or id attributes.</summary>
public sealed partial class MarkdownInjectionTests
{
	private static readonly string[] Corpus =
	[
		"# T {onmouseover=\"alert(1)\"}",
		"[x](https://example.com){onclick=\"alert(1)\"}",
		"`a.cs:1`{onclick=alert(1)}",
		"```csharp {onclick=x style=\"position:fixed\"}\nvar a = 1;\n```",
		"> q {onclick=x}",
		"text {style=\"position:fixed;inset:0\"} more",
		"| a | b |\n|---|---|\n| 1 | 2 |\n{onclick=x}",
		"- item {onfocus=x autofocus=1}",
		"![i](x){onerror=alert(1)}",
		"<script>alert(1)</script>",
		"<img src=x onerror=alert(1)>",
		"<a href=\"x\" onclick=\"y\">z</a>",
		"[x](javascript&#58;alert(1))",
		"[x](&#106;avascript:alert(1))",
		"[x](java\tscript:alert(1))",
		"[x](  javascript:alert(1))",
		"<javascript:alert(1)>",
		"[x][r]\n\n[r]: javascript:alert(1)",
		"[x](https://example.com \"t\" onclick=\"a\")",
		"```mermaid\ngraph TD; A[\"<img src=x onerror=alert(1)>\"] --> B\n```",
		"# Heading\n\n## tab-panel\n\n### app",
		"<div id=\"app\" onclick=x>",
		"&lt;script&gt;alert(1)&lt;/script&gt;",
		"<svg/onload=alert(1)>",
		"[a](https://example.com/\"onmouseover=\"alert(1))",
		"`x` {#app .y}",
		"* * *\n{onclick=x}",
		"`a.cs#Foo`{onclick=alert(1)}",
		"[x](a.cs#Foo\"onmouseover=\"alert(1))",
		"`a.cs#Foo\" style=\"x`",
	];

	public static TheoryData<string> Inputs => [.. Corpus];

	[Theory]
	[MemberData(nameof(Inputs))]
	public void Render_NeverProducesHandlersStylesIdsOrScripts(string markdown)
	{
		foreach (var streaming in new[] { false, true })
		{
			var html = MarkdownRenderer.Render(markdown, streaming);

			var markup = QuotedValue().Replace(html, "\"\"");
			Assert.DoesNotMatch(EventHandler(), markup);
			Assert.DoesNotMatch(StyleOrId(), markup);
			Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotMatch(UnsafeScheme(), html);
		}
	}

	[Theory]
	[InlineData("# T {onmouseover=\"alert(1)\"}")]
	[InlineData("`a.cs:1`{onclick=alert(1)}")]
	[InlineData("> q {onclick=x}")]
	public void Render_BraceAttributes_AreShownAsText(string markdown)
	{
		Assert.Contains("{", MarkdownRenderer.Render(markdown));
	}

	[Fact]
	public void Render_Headings_HaveNoId()
	{
		Assert.DoesNotContain("id=", MarkdownRenderer.Render("# tab-panel\n\n## app"));
	}

	/// <summary>Quoted attribute values (the diagram source sits in one, escaped) are not attributes.</summary>
	[GeneratedRegex("\"[^\"]*\"")]
	private static partial Regex QuotedValue();

	[GeneratedRegex(@"<[^>]*\son\w+\s*=", RegexOptions.IgnoreCase)]
	private static partial Regex EventHandler();

	[GeneratedRegex(@"<[^>]*\s(style|id|srcdoc|formaction)\s*=", RegexOptions.IgnoreCase)]
	private static partial Regex StyleOrId();

	[GeneratedRegex(@"(href|src)\s*=\s*""\s*(javascript|data|vbscript|file):", RegexOptions.IgnoreCase)]
	private static partial Regex UnsafeScheme();
}
