using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
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
		}
	}

	[Fact]
	public void AddApplication_ThenAddInfrastructure_BuildsWithValidation()
	{
		using (var provider = new ServiceCollection().AddApplication().AddInfrastructure(Config()).BuildServiceProvider(Strict))
		{
			Assert.NotNull(provider.GetRequiredService<CloudflareAccessTokenValidator>());
		}
	}

	private static IConfiguration Config() => new ConfigurationBuilder()
		.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["CloudflareAccess:TeamDomain"] = "team.cloudflareaccess.com",
			["CloudflareAccess:Audience"] = "aud",
		})
		.Build();
}
