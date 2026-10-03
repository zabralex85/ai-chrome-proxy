using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class TrayViewModelTests
{
	[Theory]
	[InlineData(ServiceState.NotInstalled, "Service: not installed")]
	[InlineData(ServiceState.Stopped, "Service: stopped")]
	[InlineData(ServiceState.Starting, "Service: starting…")]
	[InlineData(ServiceState.Stopping, "Service: stopping…")]
	[InlineData(ServiceState.Running, "Service: running")]
	public void Refresh_StatusLineFollowsService(ServiceState state, string text)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(text, vm.StatusText);
	}

	[Theory]
	[InlineData(ServiceState.NotInstalled, false, false, false)]
	[InlineData(ServiceState.Stopped, true, false, true)]
	[InlineData(ServiceState.Starting, false, true, true)]
	[InlineData(ServiceState.Stopping, false, false, true)]
	[InlineData(ServiceState.Running, false, true, true)]
	public void Commands_EnabledByState(ServiceState state, bool canStart, bool canStop, bool canUninstall)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(canStart, vm.StartCommand.CanExecute(null));
		Assert.Equal(canStop, vm.StopCommand.CanExecute(null));
		Assert.Equal(canStop, vm.RestartCommand.CanExecute(null));
		Assert.Equal(canUninstall, vm.UninstallCommand.CanExecute(null));
		Assert.True(vm.InstallCommand.CanExecute(null));
	}

	[Fact]
	public async Task Install_RunsElevatedAdminInstall_ThenRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var elevated = new List<string>();
		var vm = new TrayViewModel(
			service,
			command =>
			{
				elevated.Add(command);
				service.State = ServiceState.Running;
				return Task.FromResult<int?>(0);
			},
			Updates(service));
		vm.Refresh();

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal(["install"], elevated);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Uninstall_ElevatedInstanceFails_ErrorWithExitCode()
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.Running), _ => Task.FromResult<int?>(1), Updates(new FakeServiceControl()));
		vm.Refresh();

		await vm.UninstallCommand.ExecuteAsync(null);

		Assert.Equal("Service uninstall did not complete (exit code 1).", vm.Error);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(AdminCommand.Cancelled)]
	public async Task Install_UacDeclinedOrDialogCancelled_NoError(int? exitCode)
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.NotInstalled), _ => Task.FromResult(exitCode), Updates(new FakeServiceControl()));

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Restart_StopsThenStarts_AndRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = Create(service);
		vm.Refresh();

		await vm.RestartCommand.ExecuteAsync(null);

		Assert.Equal(["stop", "start"], service.Calls);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Stop_StopsService()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = Create(service);
		vm.Refresh();

		await vm.StopCommand.ExecuteAsync(null);

		Assert.Equal(["stop"], service.Calls);
		Assert.Equal(ServiceState.Stopped, vm.State);
		Assert.True(vm.StartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Start_Fails_ErrorShown_NextActionClearsIt()
	{
		var service = new FakeServiceControl(ServiceState.Stopped) { FailStart = new InvalidOperationException("access denied") };
		var vm = Create(service);
		vm.Refresh();

		await vm.StartCommand.ExecuteAsync(null);
		Assert.Equal("access denied", vm.Error);

		service.FailStart = null;
		await vm.StartCommand.ExecuteAsync(null);
		Assert.Null(vm.Error);
		Assert.Equal(ServiceState.Running, vm.State);
	}

	[Fact]
	public void Refresh_StatusQueryFails_ErrorShown()
	{
		var vm = Create(new FakeServiceControl { FailGetState = new InvalidOperationException("scm down") });

		vm.Refresh();

		Assert.Equal("scm down", vm.Error);
	}

	[Theory]
	[InlineData(ServiceState.Running)]
	[InlineData(ServiceState.Stopped)]
	public void Refresh_ServiceInTheAppFolder_AsksOnceForInstallService(ServiceState state)
	{
		var service = new FakeServiceControl(state)
		{
			BinaryPathName = "\"" + @"C:\Users\Jane Doe\AppData\Local\AiChromeProxy\current\server\AiChromeProxy.Server.exe" + "\"",
		};
		var vm = Create(service);

		vm.Refresh();

		Assert.True(vm.NeedsMigration);
		Assert.Equal(
			"Run Install service… once to move the service out of the app folder (needed to install updates with Setup.exe)",
			vm.ErrorText);
	}

	[Theory]
	[InlineData(ServiceState.Running)]
	[InlineData(ServiceState.NotInstalled)]
	public void Refresh_ServiceInDataDirectoryOrNone_NoMigrationLine(ServiceState state)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.False(vm.NeedsMigration);
		Assert.Null(vm.ErrorText);
	}

	[Fact]
	public async Task ErrorText_ActionErrorShownBeforeTheMigrationLine()
	{
		var service = new FakeServiceControl(ServiceState.Stopped)
		{
			BinaryPathName = @"C:\old\server\AiChromeProxy.Server.exe",
			FailStart = new InvalidOperationException("access denied"),
		};
		var vm = Create(service);
		vm.Refresh();

		await vm.StartCommand.ExecuteAsync(null);

		Assert.True(vm.NeedsMigration);
		Assert.Equal("access denied", vm.ErrorText);
	}

	[Theory]
	[InlineData("0.2.2+1111111", "0.2.3+2222222", "The service runs v0.2.2; the app is v0.2.3 — run Install service… to update it")]
	[InlineData("0.2.3-rc1", "0.2.3", "The service runs v0.2.3-rc1; the app is v0.2.3 — run Install service… to update it")]
	[InlineData("0.2.3+1111111", "0.2.3+2222222", null)]
	[InlineData("0.2.3", "0.2.3", null)]
	[InlineData(null, "0.2.3", null)]
	[InlineData("", "0.2.3", null)]
	[InlineData("0.2.3", null, null)]
	public void VersionText_OnlyWhenBothKnownAndDifferent(string? serviceVersion, string? appVersion, string? expected)
	{
		Assert.Equal(expected, TrayViewModel.VersionText(serviceVersion, appVersion));
	}

	[Fact]
	public void Refresh_ServiceCopyOlderThanTheApp_VersionLine()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = new TrayViewModel(service, _ => Task.FromResult<int?>(0), Updates(service), () => "0.2.2", "0.2.3");

		vm.Refresh();

		Assert.Equal("The service runs v0.2.2; the app is v0.2.3 — run Install service… to update it", vm.ErrorText);
	}

	[Fact]
	public void Refresh_SameVersion_NotInstalled_OrMigrationPending_NoVersionLine()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var version = "0.2.3";
		var vm = new TrayViewModel(service, _ => Task.FromResult<int?>(0), Updates(service), () => version, "0.2.3");

		vm.Refresh();
		Assert.Null(vm.ErrorText);

		version = "0.2.2";
		service.State = ServiceState.NotInstalled;
		vm.Refresh();
		Assert.Null(vm.ErrorText);

		service.State = ServiceState.Running;
		service.BinaryPathName = @"C:\old\server\AiChromeProxy.Server.exe";
		vm.Refresh();
		Assert.Equal(TrayViewModel.MigrationText, vm.ErrorText);
	}

	private static TrayViewModel Create(FakeServiceControl service) => new(service, _ => Task.FromResult<int?>(0), Updates(service));

	private static UpdateOrchestrator Updates(FakeServiceControl service) =>
		new(new FakeUpdateSource(service.Calls), service, Path.Combine(Path.GetTempPath(), "aicp-tests-" + Guid.NewGuid().ToString("N")));
}
