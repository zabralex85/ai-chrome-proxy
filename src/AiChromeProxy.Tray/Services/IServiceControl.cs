namespace AiChromeProxy.Tray.Services;

/// <summary>The Windows service seam: view models and the update orchestrator are unit-tested against a fake.</summary>
public interface IServiceControl
{
	ServiceState GetState();

	/// <summary>Starts the service and waits (up to 30 s) until it reports Running.</summary>
	Task StartAsync(CancellationToken ct);

	/// <summary>Stops the service and waits (up to 30 s) until it reports Stopped.</summary>
	Task StopAsync(CancellationToken ct);

	/// <summary>
	/// Elevated only. Creates (or reconfigures) the service running as <paramref name="account"/>, lets
	/// <paramref name="controlUser"/> start/stop it without UAC, prepares the data directory and starts the service.
	/// </summary>
	void Install(string account, string password, string controlUser);

	/// <summary>Elevated only. Stops and deletes the service; the data directory (settings, logs) is kept.</summary>
	void Uninstall();
}
