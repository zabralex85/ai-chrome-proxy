using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class CloudflareAccessOptionsTests
{
	[Theory]
	[InlineData("", "aud")]
	[InlineData("team.cloudflareaccess.com", "")]
	[InlineData(" ", " ")]
	public void Production_MissingConfig_Throws(string team, string aud)
	{
		var options = new CloudflareAccessOptions { TeamDomain = team, Audience = aud };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new TestHostEnvironment(Environments.Production)));

		Assert.Contains("TeamDomain", ex.Message);
	}

	[Fact]
	public void Production_Disabled_Throws()
	{
		var options = new CloudflareAccessOptions { Enabled = false };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new TestHostEnvironment(Environments.Production)));

		Assert.Contains("Development", ex.Message);
	}

	[Theory]
	[InlineData("https://team.cloudflareaccess.com")]
	[InlineData("team.cloudflareaccess.com/")]
	[InlineData("team.cloudflareaccess.com:443")]
	public void Enabled_TeamDomainNotBareHost_Throws(string team)
	{
		var options = new CloudflareAccessOptions { TeamDomain = team, Audience = "aud" };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new TestHostEnvironment(Environments.Development)));

		Assert.Contains("bare host name", ex.Message);
	}

	[Fact]
	public void Development_Disabled_Ok()
	{
		new CloudflareAccessOptions { Enabled = false }.Validate(new TestHostEnvironment(Environments.Development));
	}

	[Fact]
	public void Production_FullConfig_Ok()
	{
		new CloudflareAccessOptions { TeamDomain = "t.cloudflareaccess.com", Audience = "a" }.Validate(new TestHostEnvironment(Environments.Production));
	}
}
