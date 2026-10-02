using AiChromeProxy.Tray.Services;
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
		var vm = new TrayViewModel(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(text, vm.StatusText);
	}

	[Theory]
	[InlineData(ServiceState.NotInstalled, false, false)]
	[InlineData(ServiceState.Stopped, true, false)]
	[InlineData(ServiceState.Starting, false, true)]
	[InlineData(ServiceState.Stopping, false, false)]
	[InlineData(ServiceState.Running, false, true)]
	public void Commands_EnabledByState(ServiceState state, bool canStart, bool canStop)
	{
		var vm = new TrayViewModel(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(canStart, vm.StartCommand.CanExecute(null));
		Assert.Equal(canStop, vm.StopCommand.CanExecute(null));
		Assert.Equal(canStop, vm.RestartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Restart_StopsThenStarts_AndRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = new TrayViewModel(service);
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
		var vm = new TrayViewModel(service);
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
		var vm = new TrayViewModel(service);
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
		var vm = new TrayViewModel(new FakeServiceControl { FailGetState = new InvalidOperationException("scm down") });

		vm.Refresh();

		Assert.Equal("scm down", vm.Error);
	}
}
