using System.Net;
using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Hosting;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

var isService = WindowsServiceHelpers.IsWindowsService();

// A service starts in %WINDIR%\System32: content root (appsettings.json, wwwroot) must be the exe folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	ContentRootPath = isService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService();

var dataDir = DataDirectoryHosting.Select(isService, Environment.GetEnvironmentVariable(DataDirectory.OverrideVariable));
if (dataDir is not null)
{
	builder.Configuration.AddPersistentSettings(dataDir);
}

builder.Services.AddServerLogging(builder.Configuration, dataDir);

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, server.Port));
builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = [.. server.AllowedHosts()]);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<TunnelOptions>(builder.Configuration.GetSection(TunnelOptions.Section));
builder.Services.AddHostedService(sp => new CloudflaredSupervisor(
	sp.GetRequiredService<IOptions<TunnelOptions>>().Value,
	sp.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value,
	sp.GetRequiredService<ILogger<CloudflaredSupervisor>>(),
	sp.GetRequiredService<TimeProvider>(),
	CloudflaredProcess.Start));
builder.Services.AddSignalR();

var app = builder.Build();

var access = app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value;
try
{
	server.Validate(app.Environment);
	access.Validate(app.Environment);
}
catch (InvalidOperationException ex)
{
	// A service has no console: the log file is the only place this reason shows up.
	app.Logger.LogCritical(ex, "Invalid configuration, the Server will not start: {Reason}", ex.Message);
	throw;
}

if (!access.Enabled)
{
	app.Logger.LogWarning("Cloudflare Access check is DISABLED (Development only). Do not expose this server.");
}

app.UseMiddleware<CloudflareAccessMiddleware>();
app.MapStaticAssets();
app.MapHub<TransportHub>(TransportHub.Path);
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point; partial so WebApplicationFactory can reference it.</summary>
public partial class Program
{
}
