using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tray.Updates;

/// <summary>
/// Velopack fast callbacks: best effort, never throw, and stay inside Velopack's time limits (30 s after install, 15 s after update,
/// 30 s before uninstall). The install and after-update hooks run in the new version, as the tray user (not elevated).
/// </summary>
public static class VelopackHooks
{
	private static readonly TimeSpan AfterUpdateWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan AfterInstallStopWait = TimeSpan.FromSeconds(12);
	private static readonly TimeSpan AfterInstallStartWait = TimeSpan.FromSeconds(8);
	private static readonly TimeSpan BeforeUninstallWait = TimeSpan.FromSeconds(25);

	/// <summary>
	/// Setup.exe installed this version (also over an existing install): a service running from <c>&lt;DataDir&gt;\server</c> is stopped (the user
	/// has SERVICE_STOP), <paramref name="sync"/>ed and started again if it was running. Nothing when the service is not installed; one installed by
	/// an older version (running from the app folder) is left alone until "Install service…" moves it.
	/// </summary>
	public static void AfterInstall(IServiceControl service, Action sync)
	{
		try
		{
			if (!ServiceSetup.RunsFrom(service.GetBinaryPathName(), ServiceSetup.ServiceExecutable))
			{
				return;
			}

			var wasRunning = service.GetState() is ServiceState.Running or ServiceState.Starting;

			// Still stopping after the wait: its files are in use, so no sync (the SCM finishes the stop; the tray shows it stopped).
			if (wasRunning && !service.StopAsync(CancellationToken.None).Wait(AfterInstallStopWait))
			{
				return;
			}

			try
			{
				sync();
			}
			finally
			{
				// A failed sync left the previous copy in place: run that one rather than none.
				if (wasRunning)
				{
					service.StartAsync(CancellationToken.None).Wait(AfterInstallStartWait);
				}
			}
		}
		catch (Exception)
		{
			// The next tray start shows the state; nothing to report to from a hook.
		}
	}

	/// <summary>
	/// The new version is in place. A stopped service running from <c>&lt;DataDir&gt;\server</c> is <paramref name="sync"/>ed (the update stopped a
	/// running one first); one installed by an older version runs from the app folder, which now holds the new version. Then the service is
	/// started if the update stopped it (<paramref name="pendingMarker"/> exists; a service the user stopped stays stopped). The SCM finishes
	/// starting it even if this process is cut off; the marker is left for the restarted tray to clear.
	/// </summary>
	public static void AfterUpdate(IServiceControl service, string pendingMarker, Action sync)
	{
		try
		{
			if (ServiceSetup.RunsFrom(service.GetBinaryPathName(), ServiceSetup.ServiceExecutable) && service.GetState() == ServiceState.Stopped)
			{
				sync();
			}
		}
		catch (Exception)
		{
			// The previous copy stays in place and is started below; the next install or update syncs again.
		}

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

	/// <summary>
	/// Removes the service (<paramref name="runElevated"/>: the elevated <c>--admin uninstall</c>, after which the caller deletes its copy of the
	/// Server as the user) and the "Start with Windows" entry; settings and logs are kept.
	/// </summary>
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
