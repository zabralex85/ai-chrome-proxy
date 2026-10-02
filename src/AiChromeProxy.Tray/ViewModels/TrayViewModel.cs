using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Tray menu state: service status line and Start / Stop / Restart.</summary>
public sealed partial class TrayViewModel(IServiceControl service) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
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

	private bool CanStart() => State == ServiceState.Stopped;

	private bool CanStop() => State is ServiceState.Running or ServiceState.Starting;

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
