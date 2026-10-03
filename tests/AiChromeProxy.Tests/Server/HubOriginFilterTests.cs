using System.Net;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace AiChromeProxy.Tests.Server;

/// <summary>The hub refuses cross-site WebSocket requests: only the tunnel's own origin (plus loopback in Development) gets through.</summary>
public sealed class HubOriginFilterTests
{
	private readonly TestAccessIssuer _issuer = new();

	[Theory]
	[InlineData("https://code.example.com", HttpStatusCode.OK)]
	[InlineData(null, HttpStatusCode.Forbidden)]
	[InlineData("https://evil.example.com", HttpStatusCode.Forbidden)]
	[InlineData("http://code.example.com", HttpStatusCode.Forbidden)]
	[InlineData("https://code.example.com.evil.example.com", HttpStatusCode.Forbidden)]
	[InlineData("http://localhost:5197", HttpStatusCode.Forbidden)]
	public async Task Production_Negotiate_OnlyPublicOrigin(string? origin, HttpStatusCode expected)
	{
		using (var factory = Production())
		{
			Assert.Equal(expected, await NegotiateAsync(factory, origin, _issuer.Token()));
		}
	}

	[Theory]
	[InlineData("http://localhost:5197", HttpStatusCode.OK)]
	[InlineData("http://127.0.0.1:5197", HttpStatusCode.OK)]
	[InlineData("https://evil.example.com", HttpStatusCode.Forbidden)]
	[InlineData("http://localhost.evil.example.com:5197", HttpStatusCode.Forbidden)]
	[InlineData(null, HttpStatusCode.Forbidden)]
	public async Task Development_Negotiate_AllowsLoopbackOrigins(string? origin, HttpStatusCode expected)
	{
		using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Development);
			b.UseSetting("Server:PublicHost", string.Empty);
			b.UseSetting("CloudflareAccess:TeamDomain", string.Empty);
			b.UseSetting("CloudflareAccess:Audience", string.Empty);
		}))
		{
			Assert.Equal(expected, await NegotiateAsync(factory, origin, token: null));
		}
	}

	[Fact]
	public async Task NotHubPath_NoOriginNeeded()
	{
		using (var factory = Production())
		{
			using (var client = factory.CreateClient())
			{
				using (var request = new HttpRequestMessage(HttpMethod.Get, "/css/app.css"))
				{
					request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());
					using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
					{
						Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					}
				}
			}
		}
	}

	private static async Task<HttpStatusCode> NegotiateAsync(WebApplicationFactory<Program> factory, string? origin, string? token)
	{
		using (var client = factory.CreateClient())
		{
			using (var request = new HttpRequestMessage(HttpMethod.Post, TransportHub.Path + "/negotiate?negotiateVersion=1"))
			{
				if (origin is not null)
				{
					request.Headers.Add(HeaderNames.Origin, origin);
				}

				if (token is not null)
				{
					request.Headers.Add(CloudflareAccessMiddleware.HeaderName, token);
				}

				using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
				{
					return response.StatusCode;
				}
			}
		}
	}

	private WebApplicationFactory<Program> Production() =>
		new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseStaticWebAssets();
			b.UseSetting("Tunnel:Token", string.Empty);
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler()));
		});
}
