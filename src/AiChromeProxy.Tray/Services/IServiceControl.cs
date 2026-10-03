namespace AiChromeProxy.Tray.Services;

/// <summary>The Windows service seam: view models and the update orchestrator are unit-tested against a fake.</summary>
public interface IServiceControl
{
	ServiceState GetState();

	/// <summary>The configured binary path as the SCM stores it (quoted), or null when the service is not installed.</summary>
	string? GetBinaryPathName();

	/// <summary>Starts the service and waits (up to 30 s) until it reports Running.</summary>
	Task StartAsync(CancellationToken ct);

	/// <summary>Stops the service and waits (up to 30 s) until it reports Stopped.</summary>
	Task StopAsync(CancellationToken ct);

	/// <summary>
	/// Elevated only, SCM/LSA/DACL work only. Protects the data directory, checks that the tray copied the Server to
	/// <c>&lt;DataDir&gt;\server</c>, creates (or reconfigures) the service running from there as <paramref name="account"/>, lets
	/// <paramref name="controlUser"/> start/stop it without UAC. It does not start the service: the tray does, after a successful install.
	/// </summary>
	void Install(string account, string password, string controlUser);

	/// <summary>Elevated only. Stops and deletes the service; the non-elevated caller then deletes its copy of the Server (settings and logs are kept).</summary>
	void Uninstall();
}
