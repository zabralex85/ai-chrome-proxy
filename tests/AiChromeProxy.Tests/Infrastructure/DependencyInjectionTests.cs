using AiChromeProxy.Application;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Projects;
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class DependencyInjectionTests
{
	private static readonly ServiceProviderOptions Strict = new() { ValidateOnBuild = true, ValidateScopes = true };

	[Fact]
	public void AddInfrastructure_AloneIsSelfSufficient()
	{
		using (var provider = new ServiceCollection().AddInfrastructure(Config()).BuildServiceProvider(Strict))
		{
			Assert.NotNull(provider.GetRequiredService<CloudflareAccessTokenValidator>());
			Assert.IsType<SqliteProjectStore>(provider.GetRequiredService<IProjectStore>());
		}
	}

	[Fact]
	public void AddApplication_ThenAddInfrastructure_BuildsWithValidation()
	{
		// The host provides logging.
		using (var provider = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(Config()).BuildServiceProvider(Strict))
		{
			Assert.NotNull(provider.GetRequiredService<CloudflareAccessTokenValidator>());
			Assert.NotNull(provider.GetRequiredService<EnvelopeRouter>());
		}
	}

	private static IConfiguration Config() => new ConfigurationBuilder()
		.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["CloudflareAccess:TeamDomain"] = "team.cloudflareaccess.com",
			["CloudflareAccess:Audience"] = "aud",
			["Mirror:Root"] = Path.Combine(TempRootCleanup.Root, "di-mirror"),
			["Projects:Database"] = Path.Combine(TempRootCleanup.Root, "di-projects", "aicp.db"),
		})
		.Build();
}
