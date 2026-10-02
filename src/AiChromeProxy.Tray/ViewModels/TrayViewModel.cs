using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Tray menu state: service status line, Start / Stop / Restart and the elevated Install / Uninstall.</summary>
/// <param name="runElevated">Runs <c>--admin &lt;command&gt;</c> elevated; returns its exit code, or null when UAC was declined.</param>
public sealed partial class TrayViewModel(IServiceControl service, Func<string, Task<int?>> runElevated) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UninstallCommand))]
	public partial ServiceState State { get; private set; }

	/// <summary>Last failure of a menu action or status query; cleared when the next action starts.</summary>
	[ObservableProperty]
	public partial string? Error { get; private set; }

	public string StatusText => State switch
	{
		ServiceState.NotInstalled => "Service: not installed",
		ServiceState.Stopped => "Service: stopped",
		ServiceState.Starting => "Service: starting…",
		ServiceState.Stopping => "Service: stopping…",
		_ => "Service: running",
	};

	public void Refresh()
	{
		try
		{
			State = service.GetState();
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
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

	private bool CanStart() => State == ServiceState.Stopped;

	private bool CanUninstall() => State != ServiceState.NotInstalled;

	private bool CanStop() => State is ServiceState.Running or ServiceState.Starting;

	private async Task ElevateAsync(string command)
	{
		var exitCode = await runElevated(command);
		if (exitCode is not (null or 0))
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
