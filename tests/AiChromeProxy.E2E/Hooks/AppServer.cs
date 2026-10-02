using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.E2E.Hooks;

/// <summary>The real Server on a Kestrel listener (random loopback port) so a real browser can reach it.</summary>
public sealed class AppServer : IAsyncDisposable
{
	private readonly WebApplicationFactory<Program> _factory;

	public AppServer()
	{
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Development);
			b.UseSetting("CloudflareAccess:Enabled", "false");
			b.UseSetting("Server:Port", "0");
		});
		_factory.UseKestrel();
		_factory.StartServer();

		var addresses = _factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;
		BaseUrl = addresses.Single();
	}

	public string BaseUrl { get; }

	public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
