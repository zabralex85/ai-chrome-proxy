using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Infrastructure.Chat;
using AiChromeProxy.Infrastructure.Projects;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Infrastructure.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Infrastructure;

public static class DependencyInjection
{
	/// <summary>
	/// Registers the Cloudflare Access options (section <c>CloudflareAccess</c>), the JWKS HttpClient, the token validator, the
	/// agent runner and the Claude tools probe (section <c>Agent</c>) and the file-system mirror (section <c>Mirror</c>; the host resolves an empty <c>Root</c> with <see cref="MirrorOptions.ResolveRoot"/>).
	/// </summary>
	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<CloudflareAccessOptions>(configuration.GetSection(CloudflareAccessOptions.Section));
		services.TryAddSingleton(TimeProvider.System);
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
		services.AddSingleton<CloudflareAccessTokenValidator>();
		services.Configure<MirrorOptions>(configuration.GetSection(MirrorOptions.Section));
		services.AddSingleton<IMirrorStore, FileSystemMirrorStore>();
		services.AddSingleton<IMirrorWatcher, MirrorWatcher>();
		services.Configure<ProjectsOptions>(configuration.GetSection(ProjectsOptions.Section));
		services.AddSingleton<IProjectStore, SqliteProjectStore>();
		services.AddSingleton<IChatStore, SqliteChatStore>();
		services.AddOptions<AgentOptions>()
			.Bind(configuration.GetSection(AgentOptions.Section))
			.Validate(o => o.HasValidIdleTimeout, "Agent:IdleTimeout must be positive and at most 49 days, or -00:00:00.001 for no limit.")
			.ValidateOnStart();
		services.AddSingleton<IAgentRunner, ClaudeRunner>();
		services.AddSingleton<IClaudeToolsProbe, ClaudeToolsProbe>();
		return services;
	}
}
