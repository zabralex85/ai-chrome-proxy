using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Tray menu state: service status line, Start / Stop / Restart, the elevated Install / Uninstall and "Update to vX".</summary>
/// <param name="runElevated">Runs <c>--admin &lt;command&gt;</c> elevated; returns its exit code, or null when UAC was declined (<see cref="AdminCommand.Cancelled"/>, the dialog closed, is treated the same).</param>
/// <param name="serviceVersion">Reads the product version of the service's copy of the Server (null when unknown); null turns the version line off.</param>
/// <param name="appVersion">The tray's product version.</param>
public sealed partial class TrayViewModel(
	IServiceControl service,
	Func<string, Task<int?>> runElevated,
	UpdateOrchestrator updates,
	Func<string?>? serviceVersion = null,
	string? appVersion = null) : ObservableObject
{
	public const string MigrationText = "Run Install service… once to move the service out of the app folder (needed to install updates with Setup.exe)";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UninstallCommand))]
	public partial ServiceState State { get; private set; }

	/// <summary>Last failure of a menu action or status query; cleared when the next action starts.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ErrorText))]
	public partial string? Error { get; private set; }

	/// <summary>The installed service runs from the app folder (installed by an older version), which blocks Setup.exe from replacing it.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ErrorText))]
	public partial bool NeedsMigration { get; private set; }

	/// <summary>Newer release found by the last check; null hides the menu item.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(UpdateText), nameof(IsUpdateAvailable))]
	public partial string? UpdateVersion { get; private set; }

	/// <summary>The service's copy of the Server is another version than the tray (a hook could not update it); null when they match.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ErrorText))]
	public partial string? VersionWarning { get; private set; }

	/// <summary>The menu's error line: the last failure, else the migration request, else the version mismatch.</summary>
	public string? ErrorText => Error ?? (NeedsMigration ? MigrationText : VersionWarning);

	public string UpdateText => $"Update to v{UpdateVersion}";

	public bool IsUpdateAvailable => UpdateVersion is not null;

	public string StatusText => State switch
	{
		ServiceState.NotInstalled => "Service: not installed",
		ServiceState.Stopped => "Service: stopped",
		ServiceState.Starting => "Service: starting…",
		ServiceState.Stopping => "Service: stopping…",
		_ => "Service: running",
	};

	/// <summary>The version line, when both versions are known and differ (build metadata after <c>+</c> is ignored).</summary>
	public static string? VersionText(string? serviceVersion, string? appVersion)
	{
		var service = serviceVersion?.Split('+')[0];
		var app = appVersion?.Split('+')[0];
		return string.IsNullOrEmpty(service) || string.IsNullOrEmpty(app) || service == app
			? null
			: $"The service runs v{service}; the app is v{app} — run Install service… to update it";
	}

	public void Refresh()
	{
		try
		{
			State = service.GetState();
			NeedsMigration = State != ServiceState.NotInstalled && !ServiceSetup.RunsFrom(service.GetBinaryPathName(), ServiceSetup.ServiceExecutable);
			VersionWarning = State == ServiceState.NotInstalled || NeedsMigration || serviceVersion is null ? null : VersionText(serviceVersion(), appVersion);
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
	}

	/// <summary>Tray start-up: resume a service an interrupted update left stopped, then check for updates now and every 24 h.</summary>
	public async Task RunUpdateChecksAsync(TimeProvider time, CancellationToken ct)
	{
		await RunAsync(updates.ResumeServiceAfterUpdateAsync);
		await updates.RunChecksAsync(
			time,
			(version, error) =>
			{
				UpdateVersion = version;
				if (error is not null)
				{
					Error = $"Update check failed: {error.Message}";
				}
			},
			ct);
	}

	[RelayCommand(CanExecute = nameof(CanStart))]
	private Task StartAsync() => RunAsync(service.StartAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task StopAsync() => RunAsync(service.StopAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task RestartAsync() => RunAsync(async ct =>
	{
		await service.StopAsync(ct);
		await service.StartAsync(ct);
	});

	/// <summary>Always available: on an installed service it updates the account and password (e.g. after a Windows password change).</summary>
	[RelayCommand]
	private Task InstallAsync() => RunAsync(_ => ElevateAsync(AdminCommand.Install));

	[RelayCommand(CanExecute = nameof(CanUninstall))]
	private Task UninstallAsync() => RunAsync(_ => ElevateAsync(AdminCommand.Uninstall));

	/// <summary>Download, stop the service, apply and restart the tray; on failure the current version keeps running and the error is shown.</summary>
	[RelayCommand]
	private Task UpdateAsync() => RunAsync(updates.UpdateAsync);

	private bool CanStart() => State == ServiceState.Stopped;

	private bool CanUninstall() => State != ServiceState.NotInstalled;

	private bool CanStop() => State is ServiceState.Running or ServiceState.Starting;

	private async Task ElevateAsync(string command)
	{
		var exitCode = await runElevated(command);
		if (exitCode is not (null or 0 or AdminCommand.Cancelled))
		{
			throw new InvalidOperationException($"Service {command} did not complete (exit code {exitCode}).");
		}
	}

	private async Task RunAsync(Func<CancellationToken, Task> action)
	{
		Error = null;
		try
		{
			await action(CancellationToken.None);
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}

		Refresh();
	}
}
