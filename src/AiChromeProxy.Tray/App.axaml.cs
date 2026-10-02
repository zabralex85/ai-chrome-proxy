using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
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

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var vm = new TrayViewModel(new WindowsServiceControl());
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
