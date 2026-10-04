using AiChromeProxy.Infrastructure.Hosted;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tests.Infrastructure;
using AiChromeProxy.Tray;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Avalonia headless (no desktop window): each window's XAML loads and binds to its view model.</summary>
public sealed class WindowsSmokeTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	/// <summary>Entry point for <see cref="HeadlessUnitTestSession.StartNew(Type)"/>.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

	[Fact]
	public async Task Windows_LoadXaml_AndBind()
	{
		Directory.CreateDirectory(_dataDir.Logs);
		await File.WriteAllTextAsync(
			Path.Combine(_dataDir.Logs, "server-20261002.clef"),
			"""{"@t":"2026-10-02T10:00:00Z","@m":"Now listening"}""" + "\n",
			TestContext.Current.CancellationToken);

		var session = HeadlessUnitTestSession.StartNew(typeof(WindowsSmokeTests));
		try
		{
			await session.Dispatch(
				() =>
				{
					var settings = new SettingsWindow { DataContext = new SettingsViewModel(_dataDir, new FakeAutoStart(), new FakeServiceControl()) };
					settings.Show();
					Assert.Contains(TextBoxes(settings), t => t.Text == "5180");
					settings.Close();

					var logsVm = new LogsViewModel(_dataDir);
					var logs = new LogsWindow { DataContext = logsVm };
					logs.Show();
					Assert.Same(logsVm.Entries, logs.FindControl<ListBox>("EntryList")!.ItemsSource);
					Assert.Single(logsVm.Entries);
					logs.Close();

					var installVm = new InstallViewModel(new FakeServiceControl(ServiceState.NotInstalled), @"HOME\jane") { Password = "secret" };
					var install = new InstallWindow(installVm);
					var closed = false;
					install.Closed += (_, _) => closed = true;
					install.Show();
					Assert.Contains(TextBoxes(install), t => t.Text == @"HOME\jane");
					Assert.Contains(TextBoxes(install), t => t.PasswordChar == '●' && t.Text == "secret");
					installVm.InstallCommand.Execute(null);
					Assert.True(SpinUntil(() => closed), "install window closes once installed");

					using (var http = new HttpClient(new FakeCloudflareHandler()
						.On("GET", "user/tokens/verify", """{"status":"active"}""")
						.On("GET", "zones?status=active&page=1&per_page=50", """[{"id":"z1","name":"example.com","account":{"id":"a1","name":"Jane"}}]""", totalPages: 1)))
					{
						var wizardVm = new RemoteAccessViewModel(_dataDir, http, new HostedProvisioningClient(http), new FakeServiceControl(), _ => Task.FromResult<int?>(0), "HOMEPC") { ApiToken = "api-token" };
						var wizard = new RemoteAccessWindow { DataContext = wizardVm };
						wizard.Show();
						Assert.Contains(TextBoxes(wizard), t => t.PasswordChar == '●' && t.Text == "api-token");
						wizardVm.ContinueCommand.Execute(null);
						Assert.True(SpinUntil(() => wizardVm.IsDetailsStage), "wizard moves to the details stage");
						Assert.Equal(1, wizard.GetLogicalDescendants().OfType<ComboBox>().Single().ItemCount);
						Assert.Contains(TextBoxes(wizard), t => t.Text == "code");
						wizard.Close();
					}
				},
				TestContext.Current.CancellationToken);
		}
		finally
		{
			// The await above resumes on the Avalonia dispatcher thread; Dispose joins that thread, so it must run elsewhere.
			await Task.Run(session.Dispose, TestContext.Current.CancellationToken);
		}
	}

	public void Dispose() => Directory.Delete(_dataDir.Root, recursive: true);

	private static List<TextBox> TextBoxes(Window window) => [.. window.GetLogicalDescendants().OfType<TextBox>()];

	private static bool SpinUntil(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition() && DateTime.UtcNow < deadline)
		{
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		}

		return condition();
	}
}
