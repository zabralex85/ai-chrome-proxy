using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using Avalonia;
using Avalonia.Controls;
using Velopack;

namespace AiChromeProxy.Tray;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		// Must run first: handles Velopack's --veloapp-* hook invocations and exits.
		VelopackApp.Build()
			.SetAutoApplyOnStartup(false) // updates are applied only by UpdateOrchestrator, after it stopped the service
			.OnAfterInstallFastCallback(_ => VelopackHooks.AfterInstall(new WindowsServiceControl(), SyncServer, UpdateOrchestrator.DefaultPendingMarker))
			.OnAfterUpdateFastCallback(_ => VelopackHooks.AfterUpdate(new WindowsServiceControl(), UpdateOrchestrator.DefaultPendingMarker, SyncServer))
			.OnBeforeUninstallFastCallback(_ =>
			{
				var service = new WindowsServiceControl();
				VelopackHooks.BeforeUninstall(service, AdminCommands(service), new RegistryAutoStart());
			})
			.Run();

		switch (AdminCommand.Parse(args)?.Command)
		{
			case AdminCommand.Uninstall:
				return AdminCommand.RunUninstall(new WindowsServiceControl());
			case AdminCommand.Install:
				return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
		}

		// One tray per user session (Start with Windows plus a manual launch must not show two icons).
		using (var single = new Mutex(initiallyOwned: true, @"Local\AiChromeProxy.Tray", out var isFirst))
		{
			return isFirst ? BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown) : 0;
		}
	}

	/// <summary>Also used by the Avalonia previewer.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

	/// <summary>
	/// <c>--admin install|uninstall</c> with the file work around it done here, as the user (the elevated instance only does SCM, LSA and DACL
	/// work): the Server is copied before the install, and its copy deleted after a successful uninstall.
	/// </summary>
	internal static Func<string, Task<int?>> AdminCommands(IServiceControl service) =>
		command => ServiceSetup.RunAdminCommandAsync(command, service, SyncServer, DeleteServer, AdminCommand.RunElevatedAsync, UpdateOrchestrator.DefaultPendingMarker);

	/// <summary>The service's copy of the Server always lives in the default data directory (<c>AICP_DATA_DIR</c> is ignored).</summary>
	private static void SyncServer() => ServiceInstaller.SyncServer(DataDirectory.Resolve(null));

	private static void DeleteServer() => ServiceSetup.DeleteServerDirectory(DataDirectory.Resolve(null));
}
