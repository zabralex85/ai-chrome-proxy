using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AiChromeProxy.Infrastructure.Cloudflare;
using AiChromeProxy.Infrastructure.Hosted;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>
/// "Set up remote access": one Cloudflare API token, a zone, a subdomain and the allowed emails give a tunnel, a DNS record, an Access
/// application and the Server settings. The API token lives only in this object (never written, never logged) and is cleared on success.
/// Or an invite link: a hosted service creates the same in its own account and returns the same settings, applied the same way.
/// </summary>
/// <param name="http">Used only for <see cref="CloudflareApi.BaseUrl"/> (tests pass a fake handler).</param>
/// <param name="hosted">The hosted provisioning service behind invite links.</param>
/// <param name="runElevated">The tray's elevated <c>--admin install</c> flow: exit code, or null when UAC was declined.</param>
/// <param name="machineName">Names the tunnel (<see cref="RemoteAccessProvisioner.TunnelName"/>).</param>
public sealed partial class RemoteAccessViewModel(
	DataDirectory dataDir,
	HttpClient http,
	IHostedProvisioning hosted,
	IServiceControl service,
	Func<string, Task<int?>> runElevated,
	string machineName) : ObservableObject
{
	public const string CreateTokenUrl = "https://dash.cloudflare.com/profile/api-tokens";

	private const string InviteLinkError = "Enter the invite link you received.";

	/// <summary>Cancelled when the window closes: an abandoned setup stops and writes nothing.</summary>
	private readonly CancellationTokenSource _closed = new();

	private CloudflareApi? _api;

	/// <summary>The link text last checked on leaving the field: leaving checks each link once.</summary>
	private string? _leftLink;

	public enum WizardStage
	{
		Token,
		Details,
		Progress,
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsTokenStage), nameof(IsDetailsStage), nameof(IsProgressStage), nameof(CanGoBack), nameof(ShowsTokenInput), nameof(ShowsInviteInput))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial WizardStage Stage { get; private set; }

	public bool IsTokenStage => Stage == WizardStage.Token;

	public bool IsDetailsStage => Stage == WizardStage.Details;

	public bool IsProgressStage => Stage == WizardStage.Progress;

	/// <summary>False: the user's own Cloudflare account and an API token; true: an invite link from a hosted service.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsTokenMode), nameof(ShowsTokenInput), nameof(ShowsInviteInput), nameof(PublicUrl))]
	public partial bool IsInviteMode { get; set; }

	/// <summary>The other radio button (a binding cannot negate both ways).</summary>
	public bool IsTokenMode
	{
		get => !IsInviteMode;
		set => IsInviteMode = !value;
	}

	public bool ShowsTokenInput => IsTokenStage && !IsInviteMode;

	public bool ShowsInviteInput => IsTokenStage && IsInviteMode;

	[ObservableProperty]
	public partial string ApiToken { get; set; } = string.Empty;

	[ObservableProperty]
	public partial IReadOnlyList<CloudflareZone> Zones { get; private set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PublicUrl))]
	public partial CloudflareZone? SelectedZone { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PublicUrl))]
	public partial string Subdomain { get; set; } = "code";

	/// <summary>Allowed email addresses, comma or newline separated.</summary>
	[ObservableProperty]
	public partial string Emails { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string InviteLinkText { get; set; } = string.Empty;

	/// <summary>The invite's domain, known after a check; cleared when the link changes.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(InviteAddress), nameof(PublicUrl))]
	public partial string? InviteZone { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(InviteAddress), nameof(PublicUrl))]
	public partial string InviteSubdomain { get; set; } = string.Empty;

	/// <summary>The one address the user signs in with.</summary>
	[ObservableProperty]
	public partial string InviteEmail { get; set; } = string.Empty;

	/// <summary>"Your address: …" once the zone is known and the subdomain is valid; else empty.</summary>
	public string InviteAddress => InviteHost is { } host ? $"Your address: {host}" : string.Empty;

	/// <summary>The address the setup produces, shown live while typing; empty until a zone is chosen (or, for an invite, checked).</summary>
	public string PublicUrl => IsInviteMode
		? (InviteHost is { } host ? $"https://{host}/" : string.Empty)
		: SelectedZone is null ? string.Empty : $"https://{NormalizedSubdomain}.{SelectedZone.Name}/";

	[ObservableProperty]
	public partial IReadOnlyList<string> Errors { get; private set; } = [];

	/// <summary>One line per finished provisioning step.</summary>
	public ObservableCollection<string> Steps { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(SetUpCommand), nameof(BackCommand), nameof(CheckInviteCommand), nameof(SetUpInviteCommand))]
	public partial bool IsBusy { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial bool Failed { get; private set; }

	[ObservableProperty]
	public partial bool Succeeded { get; private set; }

	[ObservableProperty]
	public partial string? Status { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
	public partial bool IsInstallOffered { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
	public partial bool IsRestartOffered { get; private set; }

	/// <summary>Details → Token, or back to Details (Token for an invite) after a failed setup.</summary>
	public bool CanGoBack => !IsBusy && (Stage == WizardStage.Details || (Stage == WizardStage.Progress && Failed));

	// The view may set null.
	private string NormalizedSubdomain => (Subdomain ?? string.Empty).Trim().ToLowerInvariant();

	private string NormalizedInviteSubdomain => (InviteSubdomain ?? string.Empty).Trim().ToLowerInvariant();

	private string? InviteHost => InviteZone is not null && InviteSubdomainPattern().IsMatch(NormalizedInviteSubdomain) ? $"{NormalizedInviteSubdomain}.{InviteZone}" : null;

	/// <summary>
	/// First run: no <c>Server:PublicHost</c> yet, neither in the settings file nor as a <c>Server__PublicHost</c> environment variable
	/// (read through <paramref name="environment"/>, default the process's). An unreadable file counts as set up: the wizard could not write it either.
	/// </summary>
	public static bool NeedsSetup(DataDirectory dataDir, Func<string, string?>? environment = null)
	{
		if (!string.IsNullOrWhiteSpace((environment ?? Environment.GetEnvironmentVariable)($"{ServerOptions.Section}__{nameof(ServerOptions.PublicHost)}")))
		{
			return false;
		}

		try
		{
			return string.IsNullOrWhiteSpace(SettingsFile.Read(SettingsFile.LoadOrEmpty(dataDir), ServerOptions.Section, nameof(ServerOptions.PublicHost)));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>The window closed: cancel what is running and drop the API token and its client so an abandoned setup does not keep them.</summary>
	public void ForgetToken()
	{
		_closed.Cancel();
		ApiToken = string.Empty;
		_api = null;
	}

	/// <summary>Leaving the link field checks a valid link once; <b>Check</b> checks again on demand.</summary>
	public Task InviteLinkLeftAsync()
	{
		if (IsBusy || InviteZone is not null || InviteLinkText == _leftLink || !InviteLink.TryParse(InviteLinkText, out _))
		{
			return Task.CompletedTask;
		}

		_leftLink = InviteLinkText;
		return CheckInviteCommand.ExecuteAsync(null);
	}

	/// <summary>The invite subdomain rule (the service checks it again).</summary>
	[GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$")]
	private static partial Regex InviteSubdomainPattern();

	/// <summary>Token → Details: verifies the token and loads the zones it can see.</summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task ContinueAsync()
	{
		_api = null;
		Errors = [];
		if (string.IsNullOrWhiteSpace(ApiToken))
		{
			Errors = ["Paste a Cloudflare API token."];
			return;
		}

		IsBusy = true;
		try
		{
			var api = new CloudflareApi(http, ApiToken.Trim());
			await api.VerifyTokenAsync(_closed.Token);
			var zones = await api.ListZonesAsync(_closed.Token);
			_closed.Token.ThrowIfCancellationRequested();
			if (zones.Count == 0)
			{
				Errors = ["The token can see no active zone. Give it Zone: Zone Read and Zone: DNS Edit for your domain."];
				return;
			}

			_api = api;
			Zones = zones;
			SelectedZone = zones[0];
			Stage = WizardStage.Details;
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>Details → Progress: validates, provisions, then writes the four Server settings.</summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task SetUpAsync()
	{
		var subdomain = NormalizedSubdomain;
		var emails = RemoteAccessInput.ParseEmails(Emails ?? string.Empty);
		Errors = RemoteAccessInput.Validate(subdomain, SelectedZone?.Name, emails);
		if (Errors.Count > 0 || _api is not { } api || SelectedZone is not { } zone)
		{
			return;
		}

		await ProvisionAsync(cancellationToken =>
			new RemoteAccessProvisioner(api).ProvisionAsync(new RemoteAccessRequest(zone, subdomain, emails, ReadPort(), machineName), new StepProgress(Steps), cancellationToken));
	}

	/// <summary>Looks up the invite's domain for the address preview.</summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task CheckInviteAsync()
	{
		Errors = [];
		if (!InviteLink.TryParse(InviteLinkText, out var link))
		{
			Errors = [InviteLinkError];
			return;
		}

		await CheckZoneAsync(link!);
	}

	/// <summary>Invite → Progress: validates, checks the link if not done yet, redeems it, then writes the four Server settings.</summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task SetUpInviteAsync()
	{
		var subdomain = NormalizedInviteSubdomain;
		var emails = RemoteAccessInput.ParseEmails(InviteEmail);
		var errors = new List<string>();
		if (!InviteLink.TryParse(InviteLinkText, out var link))
		{
			errors.Add(InviteLinkError);
		}

		if (!InviteSubdomainPattern().IsMatch(subdomain))
		{
			errors.Add("Use lowercase letters, digits and dashes (up to 32).");
		}

		if (emails.Count != 1 || !RemoteAccessInput.IsValidEmail(emails[0]))
		{
			errors.Add("Enter one email address.");
		}

		Errors = errors;
		if (errors.Count > 0 || link is null || (InviteZone is null && !await CheckZoneAsync(link)))
		{
			return;
		}

		await ProvisionAsync(cancellationToken =>
		{
			Steps.Add("Setting up remote access…");
			return hosted.RedeemAsync(link, subdomain, emails[0], machineName, ReadPort(), cancellationToken);
		});
	}

	/// <summary>Progress (after a failure) → Details, or → Token for an invite; Details → Token.</summary>
	[RelayCommand(CanExecute = nameof(CanGoBack))]
	private void Back()
	{
		Errors = [];
		Stage = Stage == WizardStage.Progress && !IsInviteMode ? WizardStage.Details : WizardStage.Token;
	}

	[RelayCommand(CanExecute = nameof(IsInstallOffered))]
	private async Task InstallServiceAsync()
	{
		Errors = [];
		var installed = false;
		try
		{
			var exitCode = await runElevated(AdminCommand.Install);
			if (exitCode is not (null or 0 or AdminCommand.Cancelled))
			{
				throw new InvalidOperationException($"Service install did not complete (exit code {exitCode}).");
			}

			installed = exitCode == 0;
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}

		OfferService(installed);
	}

	[RelayCommand(CanExecute = nameof(IsRestartOffered))]
	private async Task RestartServiceAsync()
	{
		Errors = [];
		try
		{
			await service.StopAsync(CancellationToken.None);
			await service.StartAsync(CancellationToken.None);
			IsRestartOffered = false;
			Status = "Service restarted: remote access is live.";
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
	}

	private bool IsIdle() => !IsBusy;

	partial void OnIsInviteModeChanged(bool value) => Errors = [];

	partial void OnInviteLinkTextChanged(string value) => InviteZone = null;

	/// <summary>Both ways end here: run <paramref name="provision"/> on the Progress stage, then write the four Server settings and offer the service.</summary>
	private async Task ProvisionAsync(Func<CancellationToken, Task<RemoteAccessResult>> provision)
	{
		Steps.Clear();
		Failed = false;
		Status = null;
		Stage = WizardStage.Progress;
		IsBusy = true;
		try
		{
			var result = await provision(_closed.Token);
			_closed.Token.ThrowIfCancellationRequested();
			SettingsFile.Update(dataDir, settings =>
			{
				var access = SettingsFile.Section(settings, CloudflareAccessOptions.Section);
				access[nameof(CloudflareAccessOptions.TeamDomain)] = result.TeamDomain;
				access[nameof(CloudflareAccessOptions.Audience)] = result.Audience;
				SettingsFile.Section(settings, ServerOptions.Section)[nameof(ServerOptions.PublicHost)] = result.PublicHost;
				SettingsFile.Section(settings, TunnelOptions.Section)[nameof(TunnelOptions.Token)] = result.TunnelToken;
			});
			Steps.Add($"Settings saved to {dataDir.SettingsFile}");

			ApiToken = string.Empty;
			_api = null;
			Succeeded = true;
			OfferService();
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
			Failed = true;
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>Looks up the invite's domain; false, with the error shown, when the service refuses or cannot be reached.</summary>
	private async Task<bool> CheckZoneAsync(InviteLink link)
	{
		IsBusy = true;
		try
		{
			var zone = await hosted.GetZoneAsync(link, _closed.Token);
			_closed.Token.ThrowIfCancellationRequested();
			InviteZone = zone;
			return true;
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>
	/// Install when there is no service yet, restart a running one (it reads settings at start), else they apply at the next start.
	/// A service this wizard just <paramref name="installed"/> started with the new settings: nothing to restart.
	/// </summary>
	private void OfferService(bool installed = false)
	{
		var state = service.GetState();
		IsInstallOffered = state == ServiceState.NotInstalled;
		IsRestartOffered = !installed && state is ServiceState.Running or ServiceState.Starting;
		Status = state switch
		{
			ServiceState.NotInstalled => "Remote access is set up. Install the service to start it.",
			ServiceState.Running or ServiceState.Starting when installed => "Service installed: remote access is live.",
			ServiceState.Running or ServiceState.Starting => "Remote access is set up. Restart the service to apply.",
			_ => "Remote access is set up. It applies when the service starts.",
		};
	}

	private int ReadPort() => SettingsFile.ReadPort(SettingsFile.LoadOrEmpty(dataDir));

	/// <summary>Adds lines synchronously (the BCL <c>Progress</c> would post them to the thread pool).</summary>
	private sealed class StepProgress(ICollection<string> steps) : IProgress<string>
	{
		public void Report(string value) => steps.Add(value);
	}
}
