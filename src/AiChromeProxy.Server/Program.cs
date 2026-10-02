using System.Net;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue("Server:Port", 5180);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

builder.Services.Configure<CloudflareAccessOptions>(builder.Configuration.GetSection(CloudflareAccessOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
builder.Services.AddSingleton<CloudflareAccessTokenValidator>();
builder.Services.AddSingleton<IEnvelopeHandler, PingHandler>();
builder.Services.AddSingleton<EnvelopeRouter>();
builder.Services.AddSignalR();

var app = builder.Build();

app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value.Validate(app.Environment);

if (!app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value.Enabled)
{
	app.Logger.LogWarning("Cloudflare Access check is DISABLED (Development only). Do not expose this server.");
}

app.UseMiddleware<CloudflareAccessMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapHub<TransportHub>(TransportHub.Path);
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point; partial so WebApplicationFactory can reference it.</summary>
public partial class Program
{
}
