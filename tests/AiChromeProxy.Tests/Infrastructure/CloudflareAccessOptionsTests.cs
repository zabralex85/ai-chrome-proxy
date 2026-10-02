using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.FileProviders;
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

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new Env(Environments.Production)));

		Assert.Contains("TeamDomain", ex.Message);
	}

	[Fact]
	public void Production_Disabled_Throws()
	{
		var options = new CloudflareAccessOptions { Enabled = false };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new Env(Environments.Production)));

		Assert.Contains("Development", ex.Message);
	}

	[Fact]
	public void Development_Disabled_Ok()
	{
		new CloudflareAccessOptions { Enabled = false }.Validate(new Env(Environments.Development));
	}

	[Fact]
	public void Production_FullConfig_Ok()
	{
		new CloudflareAccessOptions { TeamDomain = "t.cloudflareaccess.com", Audience = "a" }.Validate(new Env(Environments.Production));
	}

	private sealed class Env(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;

		public string ApplicationName { get; set; } = "test";

		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}
}
