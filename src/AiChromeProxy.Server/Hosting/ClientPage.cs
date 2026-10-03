using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.StaticAssets.Infrastructure;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// Serves the Client's <c>wwwroot/index.html</c> with fingerprinted URLs from the static assets manifest: the Blazor script, the stylesheet and an
/// import map for the modules loaded by name (<c>dotnet.js</c>, which holds the boot manifest, and <c>js/fsaccess.js</c>). Those URLs are cached
/// as immutable, and the page itself is <c>no-store</c>: a proxy that stretches <c>no-cache</c> into hours cannot keep an old client alive.
/// </summary>
public static class ClientPage
{
	public const string Template = "index.html";

	/// <summary>Maps the page to <c>/index.html</c> and to every route without a file extension (the SPA fallback, <c>/</c> included).</summary>
	public static void MapClientPage(this WebApplication app)
	{
		// Rendered on first use: the manifest does not change while the Server runs.
		var page = new Lazy<string>(() => Render(ReadTemplate(app.Environment), Assets(app)));
		IResult Serve(HttpContext context)
		{
			context.Response.Headers.CacheControl = "no-store";
			return Results.Content(page.Value, "text/html; charset=utf-8");
		}

		// Ahead of the static asset endpoint (order -100) that would serve the raw template.
		app.MapGet("/" + Template, Serve).WithOrder(-1000);
		app.MapFallback(Serve);
	}

	public static string Render(string template, ResourceAssetCollection assets) => template
		.Replace("{{blazor-script}}", assets["_framework/blazor.webassembly.js"], StringComparison.Ordinal)
		.Replace("{{stylesheet}}", assets["css/app.css"], StringComparison.Ordinal)
		.Replace("{{importmap}}", ImportMapDefinition.FromResourceCollection(assets).ToString(), StringComparison.Ordinal);

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
