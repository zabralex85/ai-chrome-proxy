using System.Net;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Hosting;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Server;

/// <summary>Real Server pipeline in Production: host filtering (DNS-rebinding hardening) and fail-closed startup validation.</summary>
public sealed class ServerHostingTests : IAsyncDisposable
{
	public const string PublicHost = "code.example.com";

	private readonly TestAccessIssuer _issuer = new();
	private readonly WebApplicationFactory<Program> _factory;

	public ServerHostingTests()
	{
		_factory = Factory(b => b
			.UseSetting("Tunnel:Token", string.Empty)
			.UseSetting("Server:PublicHost", PublicHost)
			.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain)
			.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience)
			.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler())));
	}

	[Theory]
	[InlineData("evil.example.org")]
	[InlineData("code.example.com.evil.example.org")]
	[InlineData("192.168.1.10:5180")]
	public async Task UnknownHost_400_EvenWithValidToken(string host)
	{
		Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(host, _issuer.Token()));
	}

	[Theory]
	[InlineData(PublicHost)]
	[InlineData("127.0.0.1:5180")]
	[InlineData("localhost:5180")]
	public async Task KnownHost_WithToken_200(string host)
	{
		Assert.Equal(HttpStatusCode.OK, await GetAsync(host, _issuer.Token()));
	}

	[Fact]
	public void CloudflaredSupervisor_Registered()
	{
		Assert.Single(_factory.Services.GetServices<IHostedService>().OfType<CloudflaredSupervisor>());
	}

	[Fact]
	public void Production_WithoutPublicHost_FailsToStart()
	{
		// Explicit empty value: the machine running the tests may have a real Server__PublicHost env var set.
		using var factory = Factory(b => b
			.UseSetting("Server:PublicHost", string.Empty)
			.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain)
			.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience));

		var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("Server:PublicHost must be set", ex.ToString());
	}

	[Theory]
	[InlineData("Server:PublicHost", "https://code.example.com")]
	[InlineData("CloudflareAccess:TeamDomain", "https://test-team.cloudflareaccess.com/")]
	public void Production_HostNameNotBare_FailsToStart(string key, string value)
	{
		using var factory = Factory(b => b
			.UseSetting("Server:PublicHost", PublicHost)
			.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain)
			.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience)
			.UseSetting(key, value));

		var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains($"{key} must be a bare host name", ex.ToString());
	}

	public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

	private static WebApplicationFactory<Program> Factory(Action<IWebHostBuilder> configure) =>
		new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseStaticWebAssets();
			configure(b);
		});

	private async Task<HttpStatusCode> GetAsync(string host, string token)
	{
		using var client = _factory.CreateClient();
		using var request = new HttpRequestMessage(HttpMethod.Get, "/css/app.css");
		request.Headers.Host = host;
		request.Headers.Add(CloudflareAccessMiddleware.HeaderName, token);

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
		return response.StatusCode;
	}
}
