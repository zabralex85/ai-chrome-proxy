namespace AiChromeProxy.Tray.Services;

/// <summary>The Windows service seam: view models and the update orchestrator are unit-tested against a fake.</summary>
public interface IServiceControl
{
	ServiceState GetState();

	/// <summary>Starts the service and waits (up to 30 s) until it reports Running.</summary>
	Task StartAsync(CancellationToken ct);

	/// <summary>Stops the service and waits (up to 30 s) until it reports Stopped.</summary>
	Task StopAsync(CancellationToken ct);
}
