using AiChromeProxy.Tray.Services;
using Avalonia;
using Avalonia.Controls;

namespace AiChromeProxy.Tray;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		switch (AdminCommand.Parse(args)?.Command)
		{
			case AdminCommand.Uninstall:
				return AdminCommand.RunUninstall(new WindowsServiceControl());
			case AdminCommand.Install:
				return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
		}

		// One tray per user session (Start with Windows plus a manual launch must not show two icons).
		using var single = new Mutex(initiallyOwned: true, @"Local\AiChromeProxy.Tray", out var isFirst);
		return isFirst ? BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown) : 0;
	}

	/// <summary>Also used by the Avalonia previewer.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
