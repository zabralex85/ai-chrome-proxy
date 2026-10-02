using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class HostingOptionsTests
{
	[Theory]
	[InlineData("code.example.com")]
	[InlineData("team.cloudflareaccess.com")]
	[InlineData("localhost")]
	public void HostName_Bare_Valid(string value)
	{
		Assert.True(HostName.IsValid(value));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("https://team.cloudflareaccess.com")]
	[InlineData("team.cloudflareaccess.com/")]
	[InlineData("team.cloudflareaccess.com/path")]
	[InlineData("team.cloudflareaccess.com:443")]
	[InlineData("team.cloudflareaccess.com.")]
	[InlineData("127.0.0.1")]
	[InlineData("code example.com")]
	public void HostName_NotBare_Invalid(string? value)
	{
		Assert.False(HostName.IsValid(value));
	}

	[Fact]
	public void ServerOptions_Production_EmptyPublicHost_Error()
	{
		var error = new ServerOptions().GetError(isDevelopment: false);

		Assert.Contains("Server:PublicHost must be set", error);
	}

	[Fact]
	public void ServerOptions_Development_EmptyPublicHost_Ok()
	{
		Assert.Null(new ServerOptions().GetError(isDevelopment: true));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ServerOptions_InvalidPublicHost_ErrorInEveryEnvironment(bool isDevelopment)
	{
		var error = new ServerOptions { PublicHost = "https://code.example.com/" }.GetError(isDevelopment);

		Assert.Contains("bare host name", error);
	}

	[Fact]
	public void ServerOptions_Validate_ThrowsWithMessage()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => new ServerOptions().Validate(new TestHostEnvironment("Production")));

		Assert.Contains("Server:PublicHost", ex.Message);
	}

	[Fact]
	public void ServerOptions_Validate_ValidHost_Ok()
	{
		new ServerOptions { PublicHost = "code.example.com" }.Validate(new TestHostEnvironment("Production"));
	}

	[Fact]
	public void AllowedHosts_PublicHostPlusLoopback()
	{
		Assert.Equal(["code.example.com", "127.0.0.1", "localhost"], new ServerOptions { PublicHost = "code.example.com" }.AllowedHosts());
	}

	[Fact]
	public void AllowedHosts_NoPublicHost_LoopbackOnly_NeverWildcard()
	{
		Assert.Equal(["127.0.0.1", "localhost"], new ServerOptions().AllowedHosts());
	}

	[Fact]
	public void DataDirectory_DefaultsToProgramData()
	{
		var dir = DataDirectory.Resolve(null);

		Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AiChromeProxy"), dir.Root);
	}

	[Fact]
	public void DataDirectory_Override_UsedWithFixedLayout()
	{
		var dir = DataDirectory.Resolve(@"D:\data");

		Assert.Equal(@"D:\data", dir.Root);
		Assert.Equal(@"D:\data\appsettings.json", dir.SettingsFile);
		Assert.Equal(@"D:\data\logs", dir.Logs);
	}
}
