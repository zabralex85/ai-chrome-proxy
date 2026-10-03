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
			.OnAfterUpdateFastCallback(_ => VelopackHooks.AfterUpdate(new WindowsServiceControl(), UpdateOrchestrator.DefaultPendingMarker))
			.OnBeforeUninstallFastCallback(_ => VelopackHooks.BeforeUninstall(new WindowsServiceControl(), AdminCommand.RunElevatedAsync, new RegistryAutoStart()))
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
}
