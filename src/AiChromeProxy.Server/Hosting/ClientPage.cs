using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.StaticAssets.Infrastructure;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// Serves the Client's <c>wwwroot/index.html</c> with fingerprinted URLs from the static assets manifest: the Blazor script, the stylesheet and an
/// import map (line breaks are LF: the browser hashes the script after parsing, which turns CRLF into LF) for the modules loaded by name (<c>dotnet.js</c>, which holds the boot manifest, and <c>js/fsaccess.js</c>). Those URLs are cached
/// as immutable, and the page itself is <c>no-store</c>: a proxy that stretches <c>no-cache</c> into hours cannot keep an old client alive.
/// The page carries a Content-Security-Policy (<see cref="ContentSecurityPolicy"/>): scripts only from this origin plus the hashed inline import map, so
/// markup that slipped into the chat (event-handler attributes, inline scripts) cannot run.
/// </summary>
public static partial class ClientPage
{
	public const string Template = "index.html";

	/// <summary>The vendored Monaco (hundreds of files, loaded by its own AMD loader by plain paths) stays out of the import map.</summary>
	public const string UnmappedPrefix = "lib/monaco/";

	/// <summary>Maps the page to <c>/index.html</c> and to every route without a file extension (the SPA fallback, <c>/</c> included).</summary>
	public static void MapClientPage(this WebApplication app)
	{
		// Rendered on first use: the manifest does not change while the Server runs.
		var page = new Lazy<string>(() => Render(ReadTemplate(app.Environment), Assets(app)));

		// ponytail: no policy under `dotnet watch` (Development with DOTNET_WATCH=1): hot reload injects an inline script that no hash covers.
		var hotReload = app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";
		var policy = new Lazy<string>(() => ContentSecurityPolicy(page.Value));
		IResult Serve(HttpContext context)
		{
			context.Response.Headers.CacheControl = "no-store";
			if (!hotReload)
			{
				context.Response.Headers.ContentSecurityPolicy = policy.Value;
			}

			return Results.Content(page.Value, "text/html; charset=utf-8");
		}

		// Ahead of the static asset endpoint (order -100) that would serve the raw template.
		app.MapGet("/" + Template, Serve).WithOrder(-1000);
		app.MapFallback(Serve);
	}

	public static string Render(string template, ResourceAssetCollection assets) => template
		.Replace("{{blazor-script}}", assets["_framework/blazor.webassembly.js"], StringComparison.Ordinal)
		.Replace("{{stylesheet}}", assets["css/app.css"], StringComparison.Ordinal)
		.Replace("{{importmap}}", ImportMapDefinition.FromResourceCollection(new ResourceAssetCollection([.. assets.Where(a => !a.Url.StartsWith(UnmappedPrefix, StringComparison.Ordinal))])).ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);

	/// <summary>
	/// The Content-Security-Policy of the page: everything from this origin, no inline script except the ones in <paramref name="html"/> (by SHA-256, so the
	/// import map with its fingerprints is allowed and an injected inline script or event handler is not), WebAssembly allowed, inline styles allowed (mermaid's SVG,
	/// Monaco's), workers from this origin or a blob (Monaco wraps its worker in one), fonts from this origin or data (Monaco's icon font).
	/// </summary>
	/// <param name="html">The rendered page.</param>
	/// <returns>The header value.</returns>
	public static string ContentSecurityPolicy(string html)
	{
		var hashes = InlineScript().Matches(html)
			.Select(m => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value.ReplaceLineEndings("\n"))))}'");
		return "default-src 'self'; script-src 'self' 'wasm-unsafe-eval' " + string.Join(' ', hashes)
			+ "; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self' data:; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
	}

	/// <summary>A <c>&lt;script&gt;</c> without <c>src</c>; group 1 is its text.</summary>
	[GeneratedRegex(@"<script(?![^>]*\ssrc=)[^>]*>(.*?)</script>", RegexOptions.Singleline)]
	private static partial Regex InlineScript();

	/// <summary>The endpoints <c>MapStaticAssets()</c> registered, as Razor components see them (compressed variants left out).</summary>
	private static ResourceAssetCollection Assets(IEndpointRouteBuilder endpoints) =>
		new([.. StaticAssetsEndpointDataSourceHelper.ResolveStaticAssetDescriptors(endpoints, null)
			.Where(d => d.Selectors.Count == 0)
			.Select(d => new ResourceAsset(d.Route, [.. d.Properties.Select(p => new ResourceAssetProperty(p.Name, p.Value))]))]);

	private static string ReadTemplate(IWebHostEnvironment env)
	{
		using (var reader = new StreamReader(env.WebRootFileProvider.GetFileInfo(Template).CreateReadStream()))
		{
			return reader.ReadToEnd();
		}
	}
}
