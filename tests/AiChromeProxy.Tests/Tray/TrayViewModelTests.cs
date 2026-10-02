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

	[Fact]
	public async Task Install_UacDeclined_NoError()
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.NotInstalled), _ => Task.FromResult<int?>(null), Updates(new FakeServiceControl()));

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

	private static TrayViewModel Create(FakeServiceControl service) => new(service, _ => Task.FromResult<int?>(0), Updates(service));

	private static UpdateOrchestrator Updates(FakeServiceControl service) =>
		new(new FakeUpdateSource(service.Calls), service, Path.Combine(Path.GetTempPath(), "aicp-tests-" + Guid.NewGuid().ToString("N")));
}
