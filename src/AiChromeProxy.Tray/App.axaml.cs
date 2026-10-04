using System.Diagnostics;
using System.Reflection;
using AiChromeProxy.Infrastructure.Hosted;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
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

	/// <summary>The remote access wizard's connection to api.cloudflare.com (one per tray, shared by its windows).</summary>
	private static readonly HttpClient CloudflareHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

	/// <summary>The wizard's connection to an invite link's service; no overall timeout: the client sets one per call (a redeem may take a minute).</summary>
	private static readonly HostedProvisioningClient HostedProvisioning = new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			if (AdminCommand.Parse(desktop.Args) is { Command: AdminCommand.Install } admin)
			{
				ShowInstall(desktop, admin.User);
			}
			else
			{
				StartTray(desktop);
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	/// <summary>Opens <paramref name="address"/> in the default browser.</summary>
	internal static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>Product version of the service's copy of the Server; null when there is none.</summary>
	private static string? ServiceVersion() =>
		File.Exists(ServiceSetup.ServiceExecutable) ? FileVersionInfo.GetVersionInfo(ServiceSetup.ServiceExecutable).ProductVersion : null;

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	/// <summary>The elevated <c>--admin install</c> instance: only the password dialog; exit code 0 once installed.</summary>
	private static void ShowInstall(IClassicDesktopStyleApplicationLifetime desktop, string controlUser)
	{
		var vm = new InstallViewModel(new WindowsServiceControl(), controlUser);
		var window = new InstallWindow(vm);
		window.Closed += (_, _) => desktop.Shutdown(vm.ExitCode);
		window.Show();
	}

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var repository = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
		var updates = new UpdateOrchestrator(new VelopackUpdateSource(repository), service, UpdateOrchestrator.DefaultPendingMarker);
		var admin = Program.AdminCommands(service);
		var appVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		var vm = new TrayViewModel(service, admin, updates, ServiceVersion, appVersion);
		var update = new NativeMenuItem { Command = vm.UpdateCommand };
		var checkUpdates = new NativeMenuItem(vm.CheckUpdatesText) { Command = vm.CheckForUpdatesCommand };
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };

		void ShowRemoteAccess() => ShowSingle(desktop, () => new RemoteAccessWindow
		{
			DataContext = new RemoteAccessViewModel(dataDir, CloudflareHttp, HostedProvisioning, service, admin, Environment.MachineName),
		});

		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Install service…") { Command = vm.InstallCommand },
			new NativeMenuItem("Uninstall service") { Command = vm.UninstallCommand },
			new NativeMenuItemSeparator(),
			Item("Set up remote access…", ShowRemoteAccess),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Logs…", () => ShowSingle(desktop, () => new LogsWindow { DataContext = new LogsViewModel(dataDir) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			update,
			checkUpdates,
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
			error.Header = vm.ErrorText;
			error.IsVisible = vm.ErrorText is not null;
			update.Header = vm.UpdateText;
			update.IsVisible = vm.IsUpdateAvailable;
			checkUpdates.Header = vm.CheckUpdatesText;
		}

		// An update or Setup cut off between the two renames of a copy leaves the service without its folder: repair before the first status.
		try
		{
			ServiceSetup.CleanUpServerLeftovers(DataDirectory.Resolve(null));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The next sync retries.
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);
		_ = vm.RunUpdateChecksAsync(TimeProvider.System, CancellationToken.None);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);

		// First run: nothing is published yet, so lead with the wizard. Best effort after the tray is fully up: a failure here
		// must not take the tray down (the menu item still opens the wizard).
		try
		{
			if (RemoteAccessViewModel.NeedsSetup(dataDir))
			{
				ShowRemoteAccess();
			}
		}
		catch (Exception)
		{
			// No auto-open.
		}
	}
}
