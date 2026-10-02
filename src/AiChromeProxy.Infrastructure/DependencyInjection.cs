using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiChromeProxy.Infrastructure;

public static class DependencyInjection
{
	/// <summary>Registers the Cloudflare Access options (section <c>CloudflareAccess</c>), the JWKS HttpClient and the token validator.</summary>
	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<CloudflareAccessOptions>(configuration.GetSection(CloudflareAccessOptions.Section));
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
		services.AddSingleton<CloudflareAccessTokenValidator>();
		return services;
	}
}
