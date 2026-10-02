using System.Net;
using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, server.Port));
builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = [.. server.AllowedHosts()]);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSignalR();

var app = builder.Build();

server.Validate(app.Environment);
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
