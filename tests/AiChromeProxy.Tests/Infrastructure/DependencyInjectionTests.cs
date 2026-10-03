using System.Globalization;
using AiChromeProxy.Application;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Chat;
using AiChromeProxy.Infrastructure.Projects;
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
			Assert.IsType<SqliteChatStore>(provider.GetRequiredService<IChatStore>());
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

	[Theory]
	[InlineData("00:00:00")]
	[InlineData("-00:01:00")]
	[InlineData("100.00:00:00")]
	public void AgentIdleTimeout_ZeroNegativeOrTooLong_RejectedAtStart(string value)
	{
		using (var provider = new ServiceCollection().AddInfrastructure(Config(("Agent:IdleTimeout", value))).BuildServiceProvider())
		{
			var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

			Assert.Contains("Agent:IdleTimeout", ex.Message, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("00:00:05")]
	[InlineData("-00:00:00.001")]
	public void AgentIdleTimeout_PositiveOrInfinite_Accepted(string value)
	{
		using (var provider = new ServiceCollection().AddInfrastructure(Config(("Agent:IdleTimeout", value))).BuildServiceProvider())
		{
			provider.GetRequiredService<IStartupValidator>().Validate();

			Assert.Equal(TimeSpan.Parse(value, CultureInfo.InvariantCulture), provider.GetRequiredService<IOptions<AgentOptions>>().Value.IdleTimeout);
		}
	}

	private static IConfiguration Config(params (string Key, string Value)[] extra) => new ConfigurationBuilder()
		.AddInMemoryCollection(extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
		.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["CloudflareAccess:TeamDomain"] = "team.cloudflareaccess.com",
			["CloudflareAccess:Audience"] = "aud",
			["Mirror:Root"] = Path.Combine(TempRootCleanup.Root, "di-mirror"),
			["Projects:Database"] = Path.Combine(TempRootCleanup.Root, "di-projects", "aicp.db"),
		})
		.Build();
}
