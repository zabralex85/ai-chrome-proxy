using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class SettingsViewModelTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeAutoStart _autoStart = new();
	private readonly FakeServiceControl _service = new(ServiceState.Running);

	[Fact]
	public void NoSettingsFile_Defaults()
	{
		var vm = Create();

		Assert.Equal(string.Empty, vm.TeamDomain);
		Assert.Equal("5180", vm.Port);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public void OverridingVariables_AccessServerAndEnvironmentNames_CaseInsensitive_Sorted()
	{
		var environment = new Dictionary<string, string>
		{
			["Server__PublicHost"] = "code.example.com",
			["PATH"] = @"C:\Windows",
			["cloudflareaccess__Audience"] = "aud",
			["ASPNETCORE_ENVIRONMENT"] = "Development",
			["DOTNET_ENVIRONMENT"] = "Development",
			["ASPNETCORE_URLS"] = "http://+:80",
			["ServerName"] = "x",
			["Serilog__MinimumLevel"] = "Debug",
			["AICP_DATA_DIR"] = @"D:\aicp",
			["TUNNEL__TOKEN"] = "secret",
		};

		Assert.Equal(
			["AICP_DATA_DIR", "ASPNETCORE_ENVIRONMENT", "cloudflareaccess__Audience", "DOTNET_ENVIRONMENT", "Serilog__MinimumLevel", "Server__PublicHost", "TUNNEL__TOKEN"],
			SettingsViewModel.OverridingVariables(environment));
		Assert.Empty(SettingsViewModel.OverridingVariables(new Dictionary<string, string> { ["PATH"] = "x" }));
	}

	[Fact]
	public void ExistingFile_ValuesLoaded()
	{
		WriteFile("""{ "CloudflareAccess": { "TeamDomain": "t.cloudflareaccess.com", "Audience": "aud" }, "Server": { "Port": 6000, "PublicHost": "code.example.com" } }""");
		_autoStart.IsEnabled = true;

		var vm = Create();

		Assert.Equal("t.cloudflareaccess.com", vm.TeamDomain);
		Assert.Equal("aud", vm.Audience);
		Assert.Equal("6000", vm.Port);
		Assert.Equal("code.example.com", vm.PublicHost);
		Assert.True(vm.StartWithWindows);
	}

	[Fact]
	public void RootNotAnObject_ErrorShown_Defaults()
	{
		WriteFile("[]");

		var vm = Create();

		Assert.StartsWith($"Could not read {_dataDir.SettingsFile}: ", Assert.Single(vm.Errors), StringComparison.Ordinal);
		Assert.Equal("5180", vm.Port);
		Assert.Equal(string.Empty, vm.PublicHost);
	}

	[Fact]
	public void SectionsOfTheWrongShape_ReadAsText_OrDefaults()
	{
		WriteFile("""{ "CloudflareAccess": "x", "Server": { "PublicHost": 5, "Port": "6000" } }""");

		var vm = Create();

		Assert.Empty(vm.Errors);
		Assert.Equal(string.Empty, vm.TeamDomain);
		Assert.Equal("5", vm.PublicHost);
		Assert.Equal("6000", vm.Port);
	}

	[Fact]
	public void BrokenFile_ErrorShown_SaveReplacesIt()
	{
		WriteFile("{ not json");

		var vm = Valid(Create());
		Assert.Contains("Could not read", Assert.Single(vm.Errors));

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		Assert.Equal("code.example.com", (string?)ReadFile()["Server"]?["PublicHost"]);
	}

	[Theory]
	[InlineData("https://team.cloudflareaccess.com", "aud", "code.example.com", "5180", "CloudflareAccess:TeamDomain must be a bare host name")]
	[InlineData("team.cloudflareaccess.com", "", "code.example.com", "5180", "CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.")]
	[InlineData("team.cloudflareaccess.com", "aud", "", "5180", "Server:PublicHost must be set")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com:443", "5180", "Server:PublicHost must be a bare host name")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "0", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "65536", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "-1", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "abc", "Server:Port must be a number from 1 to 65535.")]
	public void Save_Invalid_ShowsServerMessage_WritesNothing(string team, string audience, string publicHost, string port, string message)
	{
		var vm = Create();
		vm.TeamDomain = team;
		vm.Audience = audience;
		vm.PublicHost = publicHost;
		vm.Port = port;

		vm.SaveCommand.Execute(null);

		Assert.StartsWith(message, Assert.Single(vm.Errors));
		Assert.False(File.Exists(_dataDir.SettingsFile));
		Assert.Null(vm.Status);
	}

	[Fact]
	public void Save_Valid_WritesExpectedJson_KeepsOtherKeys_SetsAutoStart()
	{
		WriteFile("""{ "Serilog": { "MinimumLevel": { "Default": "Debug" } }, "Server": { "Port": 5180, "Extra": true }, "Tunnel": { "Token": "secret" } }""");
		var vm = Valid(Create());
		vm.Port = " 6001 ";
		vm.StartWithWindows = true;

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		var expected = JsonNode.Parse("""
			{
			  "Serilog": { "MinimumLevel": { "Default": "Debug" } },
			  "Server": { "Port": 6001, "Extra": true, "PublicHost": "code.example.com" },
			  "Tunnel": { "Token": "secret" },
			  "CloudflareAccess": { "TeamDomain": "team.cloudflareaccess.com", "Audience": "aud" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadFile()), ReadFile().ToJsonString());
		Assert.True(_autoStart.IsEnabled);
	}

	[Fact]
	public void Save_FileNotWritable_ErrorShown_NoRestartOffered()
	{
		Directory.CreateDirectory(_dataDir.SettingsFile);
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.Contains("appsettings.json", Assert.Single(vm.Errors));
		Assert.False(vm.IsRestartOffered);
		Assert.Null(vm.Status);
	}

	[Fact]
	public async Task Save_ServiceRunning_OffersRestart_RestartStopsThenStarts()
	{
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.True(vm.IsRestartOffered);
		Assert.Equal("Saved. Restart the service to apply.", vm.Status);
		await vm.RestartServiceCommand.ExecuteAsync(null);
		Assert.Equal(["stop", "start"], _service.Calls);
		Assert.False(vm.IsRestartOffered);
		Assert.Equal("Service restarted.", vm.Status);
	}

	[Fact]
	public async Task RestartFails_ErrorShown()
	{
		_service.FailStart = new InvalidOperationException("cannot start");
		var vm = Valid(Create());
		vm.SaveCommand.Execute(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["cannot start"], vm.Errors);
	}

	[Theory]
	[InlineData(ServiceState.Stopped)]
	[InlineData(ServiceState.NotInstalled)]
	public void Save_ServiceNotRunning_NoRestartOffered(ServiceState state)
	{
		_service.State = state;
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.False(vm.IsRestartOffered);
		Assert.False(vm.RestartServiceCommand.CanExecute(null));
		Assert.Equal("Saved. Applied when the service starts.", vm.Status);
	}

	[Fact]
	public void UiAddress_PublicHost_ThroughTunnel()
	{
		WriteFile("""{ "Server": { "Port": 6000, "PublicHost": "code.example.com" } }""");

		Assert.Equal("https://code.example.com/", SettingsViewModel.UiAddress(_dataDir).AbsoluteUri);
	}

	[Theory]
	[InlineData("""{ "Server": { "Port": 6000 } }""", "http://127.0.0.1:6000/")]
	[InlineData("{ broken", "http://127.0.0.1:5180/")]
	[InlineData("[]", "http://127.0.0.1:5180/")]
	[InlineData("""{ "Server": "x" }""", "http://127.0.0.1:5180/")]
	public void UiAddress_NoPublicHost_Loopback(string json, string expected)
	{
		WriteFile(json);

		Assert.Equal(expected, SettingsViewModel.UiAddress(_dataDir).AbsoluteUri);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static SettingsViewModel Valid(SettingsViewModel vm)
	{
		vm.TeamDomain = " team.cloudflareaccess.com ";
		vm.Audience = "aud";
		vm.PublicHost = "code.example.com";
		return vm;
	}

	private SettingsViewModel Create() => new(_dataDir, _autoStart, _service);

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;
}
