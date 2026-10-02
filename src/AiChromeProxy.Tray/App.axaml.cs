using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			StartTray(desktop);
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	private static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var vm = new TrayViewModel(service);
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };
		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
