using System.Collections.ObjectModel;
using AiChromeProxy.Infrastructure.Cloudflare;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>
/// "Set up remote access": one Cloudflare API token, a zone, a subdomain and the allowed emails give a tunnel, a DNS record, an Access
/// application and the Server settings. The API token lives only in this object (never written, never logged) and is cleared on success.
/// </summary>
/// <param name="http">Used only for <see cref="CloudflareApi.BaseUrl"/> (tests pass a fake handler).</param>
/// <param name="runElevated">The tray's elevated <c>--admin install</c> flow: exit code, or null when UAC was declined.</param>
/// <param name="machineName">Names the tunnel (<see cref="RemoteAccessProvisioner.TunnelName"/>).</param>
public sealed partial class RemoteAccessViewModel(
	DataDirectory dataDir,
	HttpClient http,
	IServiceControl service,
	Func<string, Task<int?>> runElevated,
	string machineName) : ObservableObject
{
	public const string CreateTokenUrl = "https://dash.cloudflare.com/profile/api-tokens";

	private CloudflareApi? _api;

	public enum WizardStage
	{
		Token,
		Details,
		Progress,
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsTokenStage), nameof(IsDetailsStage), nameof(IsProgressStage), nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial WizardStage Stage { get; private set; }

	public bool IsTokenStage => Stage == WizardStage.Token;

	public bool IsDetailsStage => Stage == WizardStage.Details;

	public bool IsProgressStage => Stage == WizardStage.Progress;

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

	/// <summary>The address the setup produces, shown live while typing; empty until a zone is chosen.</summary>
	public string PublicUrl => SelectedZone is null ? string.Empty : $"https://{NormalizedSubdomain}.{SelectedZone.Name}/";

	[ObservableProperty]
	public partial IReadOnlyList<string> Errors { get; private set; } = [];

	/// <summary>One line per finished provisioning step.</summary>
	public ObservableCollection<string> Steps { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(SetUpCommand), nameof(BackCommand))]
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

	/// <summary>Details → Token, or back to Details after a failed setup.</summary>
	public bool CanGoBack => !IsBusy && (Stage == WizardStage.Details || (Stage == WizardStage.Progress && Failed));

	private string NormalizedSubdomain => Subdomain.Trim().ToLowerInvariant();

	/// <summary>First run: the settings file has no <c>Server:PublicHost</c> yet (an unreadable file counts as set up: the wizard could not write it either).</summary>
	public static bool NeedsSetup(DataDirectory dataDir)
	{
		try
		{
			return string.IsNullOrWhiteSpace(SettingsFile.Read(SettingsFile.LoadOrEmpty(dataDir), ServerOptions.Section, nameof(ServerOptions.PublicHost)));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>The window closed: drop the API token and its client so an abandoned setup does not keep them.</summary>
	public void ForgetToken()
	{
		ApiToken = string.Empty;
		_api = null;
	}

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
			await api.VerifyTokenAsync(CancellationToken.None);
			var zones = await api.ListZonesAsync(CancellationToken.None);
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
		var emails = RemoteAccessInput.ParseEmails(Emails);
		Errors = RemoteAccessInput.Validate(subdomain, SelectedZone?.Name, emails);
		if (Errors.Count > 0 || _api is null || SelectedZone is null)
		{
			return;
		}

		Steps.Clear();
		Failed = false;
		Status = null;
		Stage = WizardStage.Progress;
		IsBusy = true;
		try
		{
			var request = new RemoteAccessRequest(SelectedZone, subdomain, emails, ReadPort(), machineName);
			var result = await new RemoteAccessProvisioner(_api).ProvisionAsync(request, new StepProgress(Steps), CancellationToken.None);
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

	/// <summary>Progress (after a failure) → Details; Details → Token.</summary>
	[RelayCommand(CanExecute = nameof(CanGoBack))]
	private void Back()
	{
		Errors = [];
		Stage = Stage == WizardStage.Progress ? WizardStage.Details : WizardStage.Token;
	}

	[RelayCommand(CanExecute = nameof(IsInstallOffered))]
	private async Task InstallServiceAsync()
	{
		Errors = [];
		try
		{
			var exitCode = await runElevated(AdminCommand.Install);
			if (exitCode is not (null or 0 or AdminCommand.Cancelled))
			{
				throw new InvalidOperationException($"Service install did not complete (exit code {exitCode}).");
			}
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}

		OfferService();
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

	/// <summary>Install when there is no service yet, restart a running one (it reads settings at start), else they apply at the next start.</summary>
	private void OfferService()
	{
		var state = service.GetState();
		IsInstallOffered = state == ServiceState.NotInstalled;
		IsRestartOffered = state is ServiceState.Running or ServiceState.Starting;
		Status = state switch
		{
			ServiceState.NotInstalled => "Remote access is set up. Install the service to start it.",
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
