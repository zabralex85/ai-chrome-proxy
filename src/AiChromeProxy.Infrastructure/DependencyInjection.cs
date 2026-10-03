using AiChromeProxy.Application.Sync;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Infrastructure.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Infrastructure;

public static class DependencyInjection
{
	/// <summary>
	/// Registers the Cloudflare Access options (section <c>CloudflareAccess</c>), the JWKS HttpClient, the token validator and the
	/// file-system mirror (section <c>Mirror</c>; the host resolves an empty <c>Root</c> with <see cref="MirrorOptions.ResolveRoot"/>).
	/// </summary>
	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<CloudflareAccessOptions>(configuration.GetSection(CloudflareAccessOptions.Section));
		services.TryAddSingleton(TimeProvider.System);
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
		services.AddSingleton<CloudflareAccessTokenValidator>();
		services.Configure<MirrorOptions>(configuration.GetSection(MirrorOptions.Section));
		services.AddSingleton<IMirrorStore, FileSystemMirrorStore>();
		return services;
	}
}
