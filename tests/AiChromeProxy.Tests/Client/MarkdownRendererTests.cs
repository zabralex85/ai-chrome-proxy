using AiChromeProxy.Client.Chat;

namespace AiChromeProxy.Tests.Client;

public sealed class MarkdownRendererTests
{
	[Fact]
	public void Render_BasicMarkdown_IsHtml()
	{
		var html = MarkdownRenderer.Render("# Title\n\nSome **bold** text\n\n- a\n- b\n");

		Assert.Contains("<h1", html);
		Assert.Contains("<strong>bold</strong>", html);
		Assert.Contains("<li>a</li>", html);
	}

	[Fact]
	public void Render_Tables_AreSupported()
	{
		Assert.Contains("<table", MarkdownRenderer.Render("| a | b |\n|---|---|\n| 1 | 2 |\n"));
	}

	[Theory]
	[InlineData("<script>alert(1)</script>")]
	[InlineData("text <img src=x onerror=alert(1)> more")]
	[InlineData("<div onclick=\"x()\">hi</div>")]
	public void Render_RawHtml_IsEscaped(string markdown)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.DoesNotContain("<script", html);
		Assert.DoesNotContain("<img", html);
		Assert.DoesNotContain("<div onclick", html);
		Assert.Contains("&lt;", html);
	}

	[Theory]
	[InlineData("[x](javascript:alert(1))")]
	[InlineData("[x](JavaScript:alert(1))")]
	[InlineData("[x](data:text/html;base64,AAAA)")]
	[InlineData("[x](file:///C:/secret.txt)")]
	[InlineData("[x](vbscript:run)")]
	[InlineData("[x](//evil.example.com/a)")]
	[InlineData("[x](/etc/passwd:3)")]
	[InlineData("[x](../up/a.cs:3)")]
	[InlineData("[x](javascript&#58;alert(1))")]
	[InlineData("<javascript:alert(1)>")]
	public void Render_UnsafeLinks_AreDropped(string markdown)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.DoesNotContain("href", html);
	}

	[Fact]
	public void Render_UnsafeLink_KeepsItsText()
	{
		Assert.Contains(">x<", MarkdownRenderer.Render("[x](javascript:alert(1))"));
	}

	[Fact]
	public void Render_Images_AreDropped()
	{
		var html = MarkdownRenderer.Render("![alt](https://example.com/a.png)");

		Assert.DoesNotContain("<img", html);
		Assert.Contains("alt", html);
	}

	[Theory]
	[InlineData("[x](https://example.com/a?b=1)", "https://example.com/a?b=1")]
	[InlineData("[x](http://example.com)", "http://example.com")]
	[InlineData("see https://example.com/docs now", "https://example.com/docs")]
	[InlineData("<https://example.com>", "https://example.com")]
	public void Render_HttpLinks_AreKeptAndOpenSafely(string markdown, string href)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.Contains($"href=\"{href}\"", html);
		Assert.Contains("rel=\"noopener noreferrer\"", html);
		Assert.Contains("target=\"_blank\"", html);
	}

	[Theory]
	[InlineData("[Program](src/Program.cs:42)", "src/Program.cs", 42)]
	[InlineData("[a](a.cs:1)", "a.cs", 1)]
	[InlineData("`src/App/Main.razor:7`", "src/App/Main.razor", 7)]
	[InlineData("See `docs/a-b_c.md:10:3`.", "docs/a-b_c.md", 10)]
	public void Render_PathLine_BecomesAnOpenLink(string markdown, string path, int line)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.Contains($"href=\"#open={path}:{line}\"", html);
		Assert.DoesNotContain("target=", html);
	}

	[Theory]
	[InlineData("`not a path:12`")]
	[InlineData("`/etc/passwd:3`")]
	[InlineData("`../x/a.cs:3`")]
	[InlineData("`C:/x/a.cs:3`")]
	[InlineData("`src/a.cs`")]
	public void Render_InlineCodeThatIsNoPathLine_StaysCode(string markdown)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.DoesNotContain("href", html);
		Assert.Contains("<code>", html);
	}

	[Theory]
	[InlineData("[Program](src/Program.cs#Main)", "src/Program.cs", "Main")]
	[InlineData("`src/App/Main.razor#Foo.Bar`", "src/App/Main.razor", "Foo.Bar")]
	[InlineData("See `docs/a-b_c.md#_x1`.", "docs/a-b_c.md", "_x1")]
	public void Render_PathSymbol_BecomesAnOpenLink(string markdown, string path, string symbol)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.Contains($"href=\"#open={path}#{symbol}\"", html);
		Assert.DoesNotContain("target=", html);
	}

	[Theory]
	[InlineData("`src/a.cs#`")]
	[InlineData("`src/a.cs#1abc`")]
	[InlineData("`src/a.cs#a b`")]
	[InlineData("`src/a.cs#a\"onclick=x`")]
	[InlineData("`src/a.cs#a<b>`")]
	[InlineData("`/etc/passwd#Foo`")]
	[InlineData("`../x/a.cs#Foo`")]
	[InlineData("`src/../a.cs#Foo`")]
	[InlineData("`C:/x/a.cs#Foo`")]
	[InlineData("`src/a.cs#Foo#Bar`")]
	[InlineData("`src/a.cs#Foo:3`")]
	[InlineData("`#Foo`")]
	public void Render_InlineCodeThatIsNoPathSymbol_StaysCode(string markdown)
	{
		var html = MarkdownRenderer.Render(markdown);

		Assert.DoesNotContain("href", html);
		Assert.Contains("<code>", html);
	}

	[Theory]
	[InlineData("[x](src/a.cs#Foo\"onclick=\"y)")]
	[InlineData("[x](../a.cs#Foo)")]
	[InlineData("[x](javascript:alert(1)#Foo)")]
	public void Render_UnsafePathSymbolLinks_AreDropped(string markdown) => Assert.DoesNotContain("href", MarkdownRenderer.Render(markdown));

	[Theory]
	[InlineData("#open=src/a.cs:12", "src/a.cs", 12, null)]
	[InlineData("http://localhost:5000/#open=a/b.txt:3", "a/b.txt", 3, null)]
	[InlineData("#open=src/a.cs#Main", "src/a.cs", 0, "Main")]
	[InlineData("http://localhost:5000/#open=a/b.txt#A.B", "a/b.txt", 0, "A.B")]
	public void TryParseOpenLink_ReadsThePathAndTheLineOrSymbol(string uri, string path, int line, string? symbol)
	{
		Assert.True(MarkdownRenderer.TryParseOpenLink(uri, out var parsedPath, out var parsedLine, out var parsedSymbol));
		Assert.Equal((path, line, symbol), (parsedPath, parsedLine, parsedSymbol));
	}

	[Theory]
	[InlineData("http://localhost/")]
	[InlineData("http://localhost/#open=../a.cs:1")]
	[InlineData("http://localhost/#open=../a.cs#Foo")]
	[InlineData("http://localhost/#open=a.cs")]
	[InlineData("http://localhost/#open=a.cs#")]
	[InlineData("http://localhost/#open=a.cs#1x")]
	[InlineData("http://localhost/#other")]
	public void TryParseOpenLink_RejectsAnythingElse(string uri) => Assert.False(MarkdownRenderer.TryParseOpenLink(uri, out _, out _, out _));

	[Fact]
	public void Render_MermaidFence_IsMarkedForTheBrowser()
	{
		var html = MarkdownRenderer.Render("```mermaid\ngraph TD\n  A[\"x\"] --> B\n```\n");

		Assert.Contains("<div class=\"mermaid-source\" data-diagram=\"graph TD\n  A[&quot;x&quot;] --&gt; B\"></div>", html);
		Assert.DoesNotContain("<pre", html);
	}

	[Fact]
	public void Render_MermaidFenceWhileStreaming_IsPlainCode()
	{
		var html = MarkdownRenderer.Render("```mermaid\ngraph TD\n  A --> ", streaming: true);

		Assert.DoesNotContain("mermaid-source", html);
		Assert.Contains("<pre", html);
	}

	[Fact]
	public void Render_OtherFence_IsACodeBlockWithItsLanguage()
	{
		var html = MarkdownRenderer.Render("```csharp\nvar x = 1 < 2;\n```\n");

		Assert.Contains("<pre><code class=\"language-csharp\">var x = 1 &lt; 2;", html);
	}
}
