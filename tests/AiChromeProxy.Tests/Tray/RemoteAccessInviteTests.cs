using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Cloudflare;
using AiChromeProxy.Infrastructure.Hosted;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

/// <summary>The wizard's invite link mode, over a fake hosted service (no HTTP).</summary>
public sealed class RemoteAccessInviteTests : IDisposable
{
	private const string Link = "https://invites.example.com/invite/abcdefghijklmnop";
	private const string TunnelToken = "eyJhIjoiaG9zdGVkLXR1bm5lbCJ9";

	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeHosted _hosted = new();
	private readonly FakeServiceControl _service = new(ServiceState.Running);
	private readonly HttpClient _http = new();

	[Fact]
	public void Mode_Switch_ShowsTheInviteInput_ClearsErrors()
	{
		var vm = Create();
		Assert.True(vm.IsTokenMode);
		Assert.True(vm.ShowsTokenInput);
		Assert.False(vm.ShowsInviteInput);
		vm.ContinueCommand.Execute(null);
		Assert.NotEmpty(vm.Errors);

		vm.IsInviteMode = true;

		Assert.False(vm.IsTokenMode);
		Assert.False(vm.ShowsTokenInput);
		Assert.True(vm.ShowsInviteInput);
		Assert.Empty(vm.Errors);

		vm.IsTokenMode = true;

		Assert.False(vm.IsInviteMode);
		Assert.True(vm.ShowsTokenInput);
	}

	[Theory]
	[InlineData("")]
	[InlineData("http://invites.example.com/invite/abcdefghijklmnop")]
	[InlineData("https://invites.example.com/invite/short")]
	public async Task Check_InvalidLink_Error_NoCall(string link)
	{
		var vm = Invite();
		vm.InviteLinkText = link;

		await vm.CheckInviteCommand.ExecuteAsync(null);

		Assert.Equal(["Enter the invite link you received."], vm.Errors);
		Assert.Empty(_hosted.Calls);
		Assert.Null(vm.InviteZone);
	}

	[Fact]
	public async Task Check_ValidLink_ZoneKnown_AddressPreview_LinkChangeClearsIt()
	{
		var vm = Invite();
		vm.InviteLinkText = $" {Link} ";

		await vm.CheckInviteCommand.ExecuteAsync(null);

		Assert.Equal(["zone https://invites.example.com/ abcdefghijklmnop"], _hosted.Calls);
		Assert.Equal("example.com", vm.InviteZone);
		Assert.Empty(vm.InviteAddress);

		vm.InviteSubdomain = " Alice ";
		Assert.Equal("Your address: alice.example.com", vm.InviteAddress);
		Assert.Equal("https://alice.example.com/", vm.PublicUrl);

		vm.InviteSubdomain = "-alice";
		Assert.Empty(vm.InviteAddress);

		vm.InviteSubdomain = "alice";
		vm.InviteLinkText = Link + "/";
		Assert.Null(vm.InviteZone);
		Assert.Empty(vm.InviteAddress);
		Assert.Empty(vm.PublicUrl);
	}

	[Theory]
	[InlineData("a", true)]
	[InlineData("alice-2", true)]
	[InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
	[InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
	[InlineData("-a", false)]
	[InlineData("a-", false)]
	[InlineData("a_b", false)]
	[InlineData("a.b", false)]
	[InlineData("", false)]
	public async Task SubdomainRule(string subdomain, bool valid)
	{
		var vm = Invite();
		vm.InviteLinkText = Link;
		vm.InviteSubdomain = subdomain;
		await vm.CheckInviteCommand.ExecuteAsync(null);

		Assert.Equal(valid, vm.InviteAddress.Length > 0);
	}

	[Fact]
	public async Task Check_ServiceError_Shown_NotBusy()
	{
		_hosted.Zone = () => throw new HostedProvisioningException("This invite has expired.");
		var vm = Invite();
		vm.InviteLinkText = Link;

		await vm.CheckInviteCommand.ExecuteAsync(null);

		Assert.Equal(["This invite has expired."], vm.Errors);
		Assert.Null(vm.InviteZone);
		Assert.False(vm.IsBusy);
	}

	[Fact]
	public async Task LeavingTheLinkField_ChecksAValidLinkOnce()
	{
		var vm = Invite();
		vm.InviteLinkText = "not a link";
		await vm.InviteLinkLeftAsync();
		Assert.Empty(_hosted.Calls);
		Assert.Empty(vm.Errors);

		_hosted.Zone = () => throw new HostedProvisioningException("Could not reach invites.example.com: timed out.");
		vm.InviteLinkText = Link;
		await vm.InviteLinkLeftAsync();
		await vm.InviteLinkLeftAsync();
		Assert.Single(_hosted.Calls);
		Assert.Equal(["Could not reach invites.example.com: timed out."], vm.Errors);

		_hosted.Zone = () => Task.FromResult("example.com");
		await vm.CheckInviteCommand.ExecuteAsync(null);
		await vm.InviteLinkLeftAsync();
		Assert.Equal(2, _hosted.Calls.Count);
		Assert.Equal("example.com", vm.InviteZone);
	}

	[Fact]
	public async Task SetUp_InvalidInput_EveryProblemShown_NothingCalled()
	{
		var vm = Invite();
		vm.InviteLinkText = "https://invites.example.com/join/abcdefghijklmnop";
		vm.InviteSubdomain = "Alice_1";
		vm.InviteEmail = "jane@example.org, joe@example.org";

		await vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.Equal(
			["Enter the invite link you received.", "Use lowercase letters, digits and dashes (up to 32).", "Enter one email address."],
			vm.Errors);
		Assert.Empty(_hosted.Calls);
		Assert.True(vm.ShowsInviteInput);

		vm.InviteEmail = null!;
		await vm.SetUpInviteCommand.ExecuteAsync(null);
		Assert.Contains("Enter one email address.", vm.Errors);

		vm.InviteEmail = "jane";
		await vm.SetUpInviteCommand.ExecuteAsync(null);
		Assert.Contains("Enter one email address.", vm.Errors);
	}

	[Fact]
	public async Task SetUp_Success_ChecksFirst_Redeems_WritesTheFourValues_OffersRestart()
	{
		WriteSettings("""{ "Server": { "Port": 6000 }, "Serilog": { "MinimumLevel": "Debug" } }""");
		var vm = Invite();
		vm.InviteLinkText = Link;
		vm.InviteSubdomain = "alice";
		vm.InviteEmail = " jane@example.org ";

		await vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.Empty(vm.Errors);
		Assert.Equal(["zone https://invites.example.com/ abcdefghijklmnop", "redeem abcdefghijklmnop alice jane@example.org HOMEPC 6000"], _hosted.Calls);
		Assert.True(vm.Succeeded);
		Assert.True(vm.IsProgressStage);
		Assert.Equal(["Setting up remote access…", $"Settings saved to {_dataDir.SettingsFile}"], vm.Steps);
		var expected = JsonNode.Parse($$"""
			{
			  "Server": { "Port": 6000, "PublicHost": "alice.example.com" },
			  "Serilog": { "MinimumLevel": "Debug" },
			  "CloudflareAccess": { "TeamDomain": "hosted.cloudflareaccess.com", "Audience": "aud-hosted" },
			  "Tunnel": { "Token": "{{TunnelToken}}" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadSettings()), ReadSettings().ToJsonString());
		Assert.True(vm.IsRestartOffered);
		Assert.Equal("Remote access is set up. Restart the service to apply.", vm.Status);
		Assert.Equal("https://alice.example.com/", vm.PublicUrl);
		Assert.False(vm.BackCommand.CanExecute(null));
		var shown = vm.Steps.Concat(vm.Errors).Append(vm.Status ?? string.Empty);
		Assert.DoesNotContain(shown, line => line.Contains(TunnelToken, StringComparison.Ordinal));
	}

	[Fact]
	public async Task SetUp_ZoneAlreadyChecked_RedeemsOnly()
	{
		var vm = Invite();
		vm.InviteLinkText = Link;
		await vm.CheckInviteCommand.ExecuteAsync(null);
		vm.InviteSubdomain = "alice";
		vm.InviteEmail = "jane@example.org";

		await vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.Equal(2, _hosted.Calls.Count);
		Assert.StartsWith("redeem abcdefghijklmnop alice jane@example.org HOMEPC 5180", _hosted.Calls[1], StringComparison.Ordinal);
		Assert.True(vm.Succeeded);
	}

	[Fact]
	public async Task SetUp_CheckFails_ErrorShown_StaysOnTheInput_NoRedeem()
	{
		_hosted.Zone = () => throw new HostedProvisioningException("The service answered 404.");
		var vm = Ready();

		await vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.Equal(["The service answered 404."], vm.Errors);
		Assert.Single(_hosted.Calls);
		Assert.True(vm.ShowsInviteInput);
		Assert.False(vm.Failed || vm.IsBusy);
	}

	[Fact]
	public async Task SetUp_RedeemFails_ErrorShown_NoSettings_BackToTheInviteInput()
	{
		_hosted.Redeem = () => throw new HostedProvisioningException("alice.example.com is taken.");
		var vm = Ready();

		await vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.Equal(["alice.example.com is taken."], vm.Errors);
		Assert.True(vm.Failed);
		Assert.False(vm.Succeeded);
		Assert.Equal(["Setting up remote access…"], vm.Steps);
		Assert.False(File.Exists(_dataDir.SettingsFile));

		vm.BackCommand.Execute(null);

		Assert.True(vm.ShowsInviteInput);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public async Task SetUp_Busy_WhileRedeeming()
	{
		var redeem = new TaskCompletionSource<RemoteAccessResult>();
		_hosted.Redeem = () => redeem.Task;
		var vm = Ready();

		var running = vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.True(vm.IsBusy);
		Assert.False(vm.SetUpInviteCommand.CanExecute(null));
		Assert.False(vm.CheckInviteCommand.CanExecute(null));
		Assert.False(vm.BackCommand.CanExecute(null));
		redeem.SetResult(Result());
		await running;
		Assert.False(vm.IsBusy);
		Assert.True(vm.Succeeded);
	}

	[Fact]
	public async Task WindowClosedWhileRedeeming_NothingWritten()
	{
		RemoteAccessViewModel? vm = null;
		_hosted.Redeem = () =>
		{
			vm!.ForgetToken();
			return Task.FromResult(Result());
		};
		vm = Ready();

		await vm.SetUpInviteCommand.ExecuteAsync(null);

		Assert.True(vm.Failed);
		Assert.False(vm.Succeeded);
		Assert.False(File.Exists(_dataDir.SettingsFile));
	}

	public void Dispose()
	{
		_http.Dispose();
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static RemoteAccessResult Result() => new("hosted.cloudflareaccess.com", "aud-hosted", "alice.example.com", TunnelToken);

	private RemoteAccessViewModel Create() => new(_dataDir, _http, _hosted, _service, _ => Task.FromResult<int?>(0), "HOMEPC");

	private RemoteAccessViewModel Invite()
	{
		var vm = Create();
		vm.IsInviteMode = true;
		return vm;
	}

	private RemoteAccessViewModel Ready()
	{
		var vm = Invite();
		vm.InviteLinkText = Link;
		vm.InviteSubdomain = "alice";
		vm.InviteEmail = "jane@example.org";
		return vm;
	}

	private void WriteSettings(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadSettings() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;

	private sealed class FakeHosted : IHostedProvisioning
	{
		public List<string> Calls { get; } = [];

		public Func<Task<string>> Zone { get; set; } = () => Task.FromResult("example.com");

		public Func<Task<RemoteAccessResult>> Redeem { get; set; } = () => Task.FromResult(Result());

		public Task<string> GetZoneAsync(InviteLink link, CancellationToken cancellationToken)
		{
			Calls.Add($"zone {link.Origin} {link.Code}");
			return Zone();
		}

		public Task<RemoteAccessResult> RedeemAsync(InviteLink link, string subdomain, string email, string machineName, int port, CancellationToken cancellationToken)
		{
			Calls.Add($"redeem {link.Code} {subdomain} {email} {machineName} {port}");
			return Redeem();
		}
	}
}
