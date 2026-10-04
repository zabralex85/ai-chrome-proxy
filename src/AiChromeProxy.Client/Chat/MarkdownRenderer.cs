using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.GenericAttributes;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AiChromeProxy.Client.Chat;

/// <summary>
/// Claude's Markdown as HTML for the chat (shown as a <c>MarkupString</c>): advanced extensions, raw HTML switched off (it is shown as text),
/// links limited to <c>http(s)</c>, <c>path:line</c> and <c>path#Symbol</c>, images dropped. A <c>path:line</c> or <c>path#Symbol</c> (a link target, or an inline code span) becomes
/// <c>#open=path:line</c> or <c>#open=path#Symbol</c>, which the page turns into the file's tab; a fenced <c>mermaid</c> block becomes
/// <c>&lt;div class="mermaid-source" data-diagram="…"&gt;</c> that <c>Scripts/diagrams.ts</c> renders.
/// </summary>
public static partial class MarkdownRenderer
{
	private const string OpenPrefix = "#open=";

	private static readonly MarkdownPipeline Pipeline = CreatePipeline();

	/// <summary>Renders a message.</summary>
	/// <param name="markdown">Claude's text.</param>
	/// <param name="streaming">The text is still arriving: a mermaid block may be cut off, so it stays a plain code block until the stored message replaces it.</param>
	/// <returns>HTML that is safe to show as markup.</returns>
	public static string Render(string markdown, bool streaming = false)
	{
		var document = Markdown.Parse(markdown, Pipeline);
		FilterLinks(document);
		LinkPathLines(document);

		using (var writer = new StringWriter())
		{
			var renderer = new HtmlRenderer(writer);
			Pipeline.Setup(renderer);
			renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new DiagramCodeBlockRenderer(streaming));
			renderer.Render(document);
			return writer.ToString();
		}
	}

	/// <summary>Reads the <c>#open=path:line</c> or <c>#open=path#Symbol</c> link of a rendered code link out of a URI.</summary>
	/// <param name="uri">The page's URI after the click.</param>
	/// <param name="path">The repo-relative path, <c>/</c>-separated.</param>
	/// <param name="line">The line, 1-based; 0 when the link names a symbol.</param>
	/// <param name="symbol">The symbol; null when the link names a line.</param>
	/// <returns>Whether the URI is an open link.</returns>
	public static bool TryParseOpenLink(string uri, out string path, out int line, out string? symbol)
	{
		var index = uri.IndexOf(OpenPrefix, StringComparison.Ordinal);
		return TryParseTarget(index < 0 ? string.Empty : uri[(index + OpenPrefix.Length)..], out path, out line, out symbol);
	}

	/// <summary>Advanced extensions minus the two that write attributes: generic attributes (<c>{onclick=...}</c> would put event handlers and styles into the page) and auto identifiers (heading ids would clash with the shell's ids).</summary>
	private static MarkdownPipeline CreatePipeline()
	{
		var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
		builder.Extensions.TryRemove<GenericAttributesExtension>();
		builder.Extensions.TryRemove<AutoIdentifierExtension>();
		return builder.Build();
	}

	/// <summary>Parses <c>path:line</c> or <c>path#Symbol</c>; the text that comes back is rebuilt from the parts that matched, never the input.</summary>
	private static bool TryParseTarget(string text, out string path, out int line, out string? symbol)
	{
		path = string.Empty;
		line = 0;
		symbol = null;
		// Markdig percent-encodes non-ASCII in href. Everything below validates the unescaped text; a "%" left over (double encoding) fails the pattern.
		var match = PathTarget().Match(Uri.UnescapeDataString(text));
		if (!match.Success || match.Groups["path"].Value.Split('/').Any(s => s.All(c => c == '.')))
		{
			return false;
		}

		if (match.Groups["symbol"].Success)
		{
			symbol = match.Groups["symbol"].Value;
		}
		else if (!int.TryParse(match.Groups["line"].Value, out line))
		{
			return false;
		}

		path = match.Groups["path"].Value;
		return true;
	}

	private static string OpenTarget(string path, int line, string? symbol) => symbol is null ? $"{OpenPrefix}{path}:{line}" : $"{OpenPrefix}{path}#{symbol}";

	private static bool IsHttp(string? url) =>
		url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

	private static void FilterLinks(MarkdownDocument document)
	{
		foreach (var link in document.Descendants<LinkInline>().ToList())
		{
			if (link.IsImage)
			{
				Unlink(link);
			}
			else if (IsHttp(link.Url))
			{
				OpenInNewTab(link);
			}
			else if (link.Url is not null && TryParseTarget(link.Url, out var path, out var line, out var symbol))
			{
				link.Url = OpenTarget(path, line, symbol);
			}
			else
			{
				Unlink(link);
			}
		}

		foreach (var auto in document.Descendants<AutolinkInline>().ToList())
		{
			if (IsHttp(auto.Url))
			{
				var link = new LinkInline(auto.Url, string.Empty);
				link.AppendChild(new LiteralInline(auto.Url));
				auto.ReplaceBy(link);
				OpenInNewTab(link);
			}
			else
			{
				auto.ReplaceBy(new LiteralInline(auto.Url));
			}
		}
	}

	private static void OpenInNewTab(LinkInline link)
	{
		var attributes = link.GetAttributes();
		attributes.AddPropertyIfNotExist("target", "_blank");
		attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
	}

	/// <summary>The link's text stays, the link goes.</summary>
	private static void Unlink(LinkInline link)
	{
		foreach (var child in link.ToList())
		{
			child.Remove();
			link.InsertBefore(child);
		}

		link.Remove();
	}

	private static void LinkPathLines(MarkdownDocument document)
	{
		foreach (var code in document.Descendants<CodeInline>().ToList())
		{
			if (code.Parent is not LinkInline && TryParseTarget(code.Content, out var path, out var line, out var symbol))
			{
				var link = new LinkInline(OpenTarget(path, line, symbol), string.Empty);
				code.ReplaceBy(link);
				link.AppendChild(code);
			}
		}
	}

	[GeneratedRegex(@"^(?<path>(?!/)(?:[\w@+.\-]+/)*[\w@+\-][\w@+.\-]*\.\w+)(?::(?<line>\d{1,9})(?::\d+)?|#(?<symbol>[A-Za-z_][\w.]*))$")]
	private static partial Regex PathTarget();

	private sealed class DiagramCodeBlockRenderer(bool streaming) : CodeBlockRenderer
	{
		protected override void Write(HtmlRenderer renderer, CodeBlock obj)
		{
			if (!streaming && obj is FencedCodeBlock { Info: "mermaid" })
			{
				renderer.Write("<div class=\"mermaid-source\" data-diagram=\"").WriteEscape(obj.Lines.ToString().ReplaceLineEndings("\n")).Write("\"></div>\n");
				return;
			}

			base.Write(renderer, obj);
		}
	}
}
