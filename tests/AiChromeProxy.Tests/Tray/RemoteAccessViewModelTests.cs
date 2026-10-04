using System.Net;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosted;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tests.Infrastructure;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using static AiChromeProxy.Tray.ViewModels.RemoteAccessViewModel;

namespace AiChromeProxy.Tests.Tray;

public sealed class RemoteAccessViewModelTests : IDisposable
{
	private const string Zones = """[{"id":"z1","name":"example.com","account":{"id":"a1","name":"Jane"}},{"id":"z2","name":"example.org","account":{"id":"a1","name":"Jane"}}]""";

	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeCloudflareHandler _handler = new();
	private readonly HttpClient _http;
	private readonly FakeServiceControl _service = new(ServiceState.Running);
	private readonly List<string> _elevated = [];
	private int? _elevatedExitCode = 0;

	public RemoteAccessViewModelTests()
	{
		_http = new HttpClient(_handler);
		_handler
			.On("GET", "user/tokens/verify", """{"id":"t","status":"active"}""")
			.On("GET", "zones?status=active&page=1&per_page=50", Zones, totalPages: 1);
	}

	[Fact]
	public void Defaults_TokenStage_CodeSubdomain()
	{
		var vm = Create();

		Assert.True(vm.IsTokenStage);
		Assert.False(vm.IsDetailsStage || vm.IsProgressStage);
		Assert.Equal("code", vm.Subdomain);
		Assert.Equal(string.Empty, vm.PublicUrl);
		Assert.False(vm.BackCommand.CanExecute(null));
	}

	[Fact]
	public async Task Continue_NoToken_Error_NoRequest()
	{
		var vm = Create();
		vm.ApiToken = " ";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Equal(["Paste a Cloudflare API token."], vm.Errors);
		Assert.Empty(_handler.Requests);
		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task Continue_ValidToken_DetailsWithZones_PublicUrlFollowsInput()
	{
		var vm = Create();
		vm.ApiToken = " api-token ";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Empty(vm.Errors);
		Assert.True(vm.IsDetailsStage);
		Assert.Equal(["example.com", "example.org"], vm.Zones.Select(z => z.Name));
		Assert.Equal("Bearer api-token", _handler.Requests[0].Authorization);
		Assert.Equal("https://code.example.com/", vm.PublicUrl);

		vm.Subdomain = " Dev ";
		vm.SelectedZone = vm.Zones[1];

		Assert.Equal("https://dev.example.org/", vm.PublicUrl);
	}

	[Fact]
	public async Task Continue_RejectedToken_ErrorShown_StaysOnTokenStage()
	{
		_handler.OnError("GET", "user/tokens/verify", HttpStatusCode.Unauthorized, 1000, "Invalid API Token");
		var vm = Create();
		vm.ApiToken = "bad";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Equal(["Cloudflare API error 1000 on GET user/tokens/verify: Invalid API Token"], vm.Errors);
		Assert.True(vm.IsTokenStage);
		Assert.False(vm.IsBusy);
	}

	[Fact]
	public async Task Continue_NoZones_ExplainsPermissions()
	{
		_handler.On("GET", "zones?status=active&page=1&per_page=50", "[]", totalPages: 0);
		var vm = Create();
		vm.ApiToken = "t";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Contains("no active zone", Assert.Single(vm.Errors), StringComparison.Ordinal);
		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task Back_FromDetails_ToToken()
	{
		var vm = await DetailsAsync();

		vm.BackCommand.Execute(null);

		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task SetUp_InvalidDetails_InlineErrors_NothingProvisioned()
	{
		var vm = await DetailsAsync();
		vm.Subdomain = "-bad";
		vm.Emails = "jane";
		var requests = _handler.Requests.Count;

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Equal(
			["The subdomain must be one label of a-z, 0-9 and '-', 1 to 63 characters, not starting or ending with '-'.", "'jane' is not an email address."],
			vm.Errors);
		Assert.True(vm.IsDetailsStage);
		Assert.Equal(requests, _handler.Requests.Count);
	}

	[Fact]
	public async Task SetUp_Success_WritesTheFourValues_KeepsOtherKeys_ClearsApiToken_OffersRestart()
	{
		WriteSettings("""{ "Server": { "Port": 6000 }, "Serilog": { "MinimumLevel": "Debug" } }""");
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com,\njoe@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Empty(vm.Errors);
		Assert.True(vm.Succeeded);
		Assert.True(vm.IsProgressStage);
		Assert.Equal(string.Empty, vm.ApiToken);
		Assert.Equal(7, vm.Steps.Count);
		Assert.Equal($"Settings saved to {_dataDir.SettingsFile}", vm.Steps[^1]);
		Assert.Equal("http://127.0.0.1:6000", (string?)_handler.Body("PUT", $"accounts/a1/cfd_tunnel/{RemoteAccessProvisionerTests.TunnelId}/configurations")!["config"]!["ingress"]![0]!["service"]);
		var expected = JsonNode.Parse($$"""
			{
			  "Server": { "Port": 6000, "PublicHost": "code.example.com" },
			  "Serilog": { "MinimumLevel": "Debug" },
			  "CloudflareAccess": { "TeamDomain": "jane.cloudflareaccess.com", "Audience": "aud-123" },
			  "Tunnel": { "Token": "{{RemoteAccessProvisionerTests.TunnelToken}}" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadSettings()), ReadSettings().ToJsonString());
		Assert.True(vm.IsRestartOffered);
		Assert.False(vm.IsInstallOffered);
		Assert.Equal("Remote access is set up. Restart the service to apply.", vm.Status);
		Assert.False(vm.BackCommand.CanExecute(null));
	}

	[Fact]
	public async Task Restart_StopsThenStarts()
	{
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["stop", "start"], _service.Calls);
		Assert.False(vm.IsRestartOffered);
		Assert.Equal("Service restarted: remote access is live.", vm.Status);
	}

	[Fact]
	public async Task Restart_Fails_ErrorShown()
	{
		_service.FailStart = new InvalidOperationException("cannot start");
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["cannot start"], vm.Errors);
	}

	[Fact]
	public async Task SetUp_Success_NoService_OffersInstall_RunsElevatedInstall()
	{
		_service.State = ServiceState.NotInstalled;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);
		Assert.True(vm.IsInstallOffered);
		Assert.Equal("Remote access is set up. Install the service to start it.", vm.Status);

		_service.State = ServiceState.Running;
		await vm.InstallServiceCommand.ExecuteAsync(null);

		Assert.Equal([AdminCommand.Install], _elevated);
		Assert.False(vm.IsInstallOffered);
		Assert.Empty(vm.Errors);

		// The service it started read the new settings: nothing to restart.
		Assert.False(vm.IsRestartOffered);
		Assert.Equal("Service installed: remote access is live.", vm.Status);
	}

	[Fact]
	public async Task Install_UacDeclined_StillOffered()
	{
		_service.State = ServiceState.NotInstalled;
		_elevatedExitCode = null;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.InstallServiceCommand.ExecuteAsync(null);

		Assert.True(vm.IsInstallOffered);
		Assert.Equal("Remote access is set up. Install the service to start it.", vm.Status);
	}

	[Fact]
	public async Task Install_FailedExitCode_ErrorShown()
	{
		_service.State = ServiceState.NotInstalled;
		_elevatedExitCode = 5;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.InstallServiceCommand.ExecuteAsync(null);

		Assert.Equal(["Service install did not complete (exit code 5)."], vm.Errors);
		Assert.True(vm.IsInstallOffered);
	}

	[Fact]
	public async Task SetUp_Success_ServiceStopped_AppliesAtNextStart()
	{
		_service.State = ServiceState.Stopped;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.False(vm.IsInstallOffered || vm.IsRestartOffered);
		Assert.Equal("Remote access is set up. It applies when the service starts.", vm.Status);
	}

	[Fact]
	public async Task SetUp_ProvisioningFails_ErrorAndBack_NoSettingsWritten()
	{
		RemoteAccessProvisionerTests.FreshAccount(_handler)
			.On("GET", "zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50", """[{"id":"d9","type":"A","content":"203.0.113.10","proxied":true}]""", totalPages: 1);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Equal(["code.example.com already has a DNS record; choose another subdomain or delete it."], vm.Errors);
		Assert.True(vm.Failed);
		Assert.False(vm.Succeeded);
		Assert.Single(vm.Steps);
		Assert.False(File.Exists(_dataDir.SettingsFile));
		Assert.Equal("api-token", vm.ApiToken);

		vm.BackCommand.Execute(null);

		Assert.True(vm.IsDetailsStage);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public void NeedsSetup_OnlyWithoutPublicHost()
	{
		Assert.True(NeedsSetup(_dataDir));

		WriteSettings("""{ "Server": { "Port": 5180 } }""");
		Assert.True(NeedsSetup(_dataDir));

		WriteSettings("{ broken");
		Assert.True(NeedsSetup(_dataDir));

		WriteSettings("""{ "Server": { "PublicHost": "code.example.com" } }""");
		Assert.False(NeedsSetup(_dataDir));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("""{ "Server": "x" }""")]
	[InlineData("""{ "Server": { "PublicHost": { "a": 1 } } }""")]
	public void NeedsSetup_SettingsOfTheWrongShape_True(string json)
	{
		WriteSettings(json);

		Assert.True(NeedsSetup(_dataDir));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("""{ "Server": "x" }""")]
	[InlineData("""{ "Server": { "Port": [6000] } }""")]
	public async Task SetUp_SettingsOfTheWrongShape_DefaultPort_FileReplaced(string json)
	{
		WriteSettings(json);
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Empty(vm.Errors);
		Assert.True(vm.Succeeded);
		Assert.Equal("http://127.0.0.1:5180", (string?)_handler.Body("PUT", $"accounts/a1/cfd_tunnel/{RemoteAccessProvisionerTests.TunnelId}/configurations")!["config"]!["ingress"]![0]!["service"]);
		Assert.Equal("code.example.com", (string?)ReadSettings()["Server"]!["PublicHost"]);
	}

	[Fact]
	public async Task Secrets_NeverInStepsStatusOrErrors()
	{
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.True(vm.Succeeded);
		var shown = vm.Steps.Concat(vm.Errors).Append(vm.Status ?? string.Empty).ToList();
		Assert.DoesNotContain(shown, line => line.Contains("api-token", StringComparison.Ordinal));
		Assert.DoesNotContain(shown, line => line.Contains(RemoteAccessProvisionerTests.TunnelToken, StringComparison.Ordinal));
	}

	[Fact]
	public async Task Continue_NetworkFailure_ErrorShown_NotBusy()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => throw new HttpRequestException("No such host is known. (api.cloudflare.com:443)"));
		var vm = Create();
		vm.ApiToken = "api-token";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Equal(["No such host is known. (api.cloudflare.com:443)"], vm.Errors);
		Assert.False(vm.IsBusy);
		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task Continue_AgainWithARejectedToken_ForgetsTheEarlierClient()
	{
		var vm = await DetailsAsync();
		vm.BackCommand.Execute(null);
		_handler.OnError("GET", "user/tokens/verify", HttpStatusCode.Unauthorized, 1000, "Invalid API Token");
		vm.ApiToken = "bad";
		await vm.ContinueCommand.ExecuteAsync(null);
		vm.Emails = "jane@example.com";
		var requests = _handler.Requests.Count;

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Equal(requests, _handler.Requests.Count);
		Assert.False(vm.IsProgressStage);
	}

	[Fact]
	public async Task ForgetToken_ClearsTokenAndClient()
	{
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		var requests = _handler.Requests.Count;

		vm.ForgetToken();
		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Equal(string.Empty, vm.ApiToken);
		Assert.Equal(requests, _handler.Requests.Count);
		Assert.False(vm.IsProgressStage);
	}

	[Fact]
	public void NeedsSetup_UnreadableFile_False()
	{
		WriteSettings("{}");
		using (var locked = File.Open(_dataDir.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			Assert.False(NeedsSetup(_dataDir));
		}
	}

	[Fact]
	public async Task WindowClosedWhileSettingUp_Cancelled_NoSettingsWritten()
	{
		RemoteAccessViewModel? vm = null;
		RemoteAccessProvisionerTests.FreshAccount(_handler).OnResponse("POST", "accounts/a1/access/apps", () =>
		{
			// The last request is in flight when the window closes.
			vm!.ForgetToken();
			return FakeCloudflareHandler.Json(HttpStatusCode.OK, FakeCloudflareHandler.Envelope("""{"id":"app1","domain":"code.example.com","aud":"aud-123"}"""));
		});
		vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.False(vm.Succeeded);
		Assert.True(vm.Failed);
		Assert.False(File.Exists(_dataDir.SettingsFile));
	}

	[Fact]
	public async Task WindowClosedWhileCheckingToken_Cancelled_StaysOnTokenStage()
	{
		RemoteAccessViewModel? vm = null;
		_handler.OnResponse("GET", "user/tokens/verify", () =>
		{
			vm!.ForgetToken();
			return FakeCloudflareHandler.Json(HttpStatusCode.OK, FakeCloudflareHandler.Envelope("""{"id":"t","status":"active"}"""));
		});
		vm = Create();
		vm.ApiToken = "api-token";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.True(vm.IsTokenStage);
		Assert.DoesNotContain(_handler.Calls, c => c.StartsWith("GET zones", StringComparison.Ordinal));
	}

	[Fact]
	public async Task NullInput_FromTheView_ValidationErrors_NoCrash()
	{
		var vm = await DetailsAsync();

		vm.Subdomain = null!;
		vm.Emails = null!;

		Assert.Equal("https://.example.com/", vm.PublicUrl);
		await vm.SetUpCommand.ExecuteAsync(null);
		Assert.NotEmpty(vm.Errors);
		Assert.True(vm.IsDetailsStage);
	}

	[Fact]
	public void NeedsSetup_PublicHostFromTheEnvironment_False()
	{
		Assert.False(RemoteAccessViewModel.NeedsSetup(_dataDir, name => name == "Server__PublicHost" ? "code.example.com" : null));
		Assert.True(RemoteAccessViewModel.NeedsSetup(_dataDir, name => name == "Server__PublicHost" ? " " : null));
	}

	public void Dispose()
	{
		_http.Dispose();
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	/// <summary>Without environment variables, whatever the test machine has.</summary>
	private static bool NeedsSetup(DataDirectory dataDir) => RemoteAccessViewModel.NeedsSetup(dataDir, _ => null);

	private RemoteAccessViewModel Create() =>
		new(_dataDir, _http, new HostedProvisioningClient(_http), _service, command =>
		{
			_elevated.Add(command);
			return Task.FromResult(_elevatedExitCode);
		}, "HOMEPC");

	private async Task<RemoteAccessViewModel> DetailsAsync()
	{
		var vm = Create();
		vm.ApiToken = "api-token";
		await vm.ContinueCommand.ExecuteAsync(null);
		Assert.True(vm.IsDetailsStage);
		return vm;
	}

	private void WriteSettings(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadSettings() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;
}
