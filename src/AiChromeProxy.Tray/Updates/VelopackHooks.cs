using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tray.Updates;

/// <summary>Velopack fast callbacks: best effort, never throw, and stay inside Velopack's time limits (15 s after update, 30 s before uninstall).</summary>
public static class VelopackHooks
{
	private static readonly TimeSpan AfterUpdateWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan BeforeUninstallWait = TimeSpan.FromSeconds(25);

	/// <summary>
	/// The new version is in place: start the service if the update stopped it (<paramref name="pendingMarker"/> exists; a service the user
	/// stopped stays stopped). The SCM finishes starting it even if this process is cut off; the marker is left for the restarted tray to clear.
	/// </summary>
	public static void AfterUpdate(IServiceControl service, string pendingMarker)
	{
		try
		{
			if (File.Exists(pendingMarker) && service.GetState() == ServiceState.Stopped)
			{
				service.StartAsync(CancellationToken.None).Wait(AfterUpdateWait);
			}
		}
		catch (Exception)
		{
			// The next tray start shows the state; nothing to report to from a hook.
		}
	}

	/// <summary>Removes the service (elevated <c>--admin uninstall</c>) and the "Start with Windows" entry; the data directory is kept.</summary>
	public static void BeforeUninstall(IServiceControl service, Func<string, Task<int?>> runElevated, IAutoStart autoStart)
	{
		try
		{
			autoStart.IsEnabled = false;
			if (service.GetState() != ServiceState.NotInstalled)
			{
				runElevated(AdminCommand.Uninstall).Wait(BeforeUninstallWait);
			}
		}
		catch (Exception)
		{
			// Uninstall must not fail because of the service; it can be removed later with "sc delete AiChromeProxy".
		}
	}
}
