using System.Threading.Channels;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using AiChromeProxy.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Tray;

public sealed class UpdateOrchestratorTests : IDisposable
{
	private const string OldLayout = "\"" + @"C:\Users\Jane Doe\AppData\Local\AiChromeProxy\current\server\AiChromeProxy.Server.exe" + "\"";

	private readonly string _marker = Path.Combine(Path.GetTempPath(), "aicp-tests-" + Guid.NewGuid().ToString("N") + ".update-pending");
	private readonly FakeServiceControl _service = new(ServiceState.Running);
	private readonly FakeUpdateSource _source;
	private readonly UpdateOrchestrator _updates;

	public UpdateOrchestratorTests()
	{
		_source = new FakeUpdateSource(_service.Calls);
		_updates = new UpdateOrchestrator(_source, _service, _marker);
	}

	[Fact]
	public async Task Update_DownloadThenStopThenApply()
	{
		await _updates.UpdateAsync(TestContext.Current.CancellationToken);

		Assert.Equal(["download", "stop", "apply"], _service.Calls);
		Assert.True(File.Exists(_marker));
	}

	[Fact]
	public async Task DownloadFails_ServiceUntouched()
	{
		_source.FailDownload = new HttpRequestException("offline");

		await Assert.ThrowsAsync<HttpRequestException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["download"], _service.Calls);
		Assert.Equal(ServiceState.Running, _service.State);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task ApplyFails_ServiceStartedAgain()
	{
		_source.FailApply = new InvalidOperationException("locked");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["download", "stop", "apply", "start"], _service.Calls);
		Assert.Equal(ServiceState.Running, _service.State);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task ApplyFails_RestartFails_ApplyErrorRethrown_MarkerDeleted()
	{
		var applyError = new InvalidOperationException("locked");
		_source.FailApply = applyError;
		_service.FailStart = new InvalidOperationException("timeout");

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Same(applyError, thrown);
		Assert.Equal(["download", "stop", "apply", "start"], _service.Calls);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task StopFails_NotApplied_StartAttempted_MarkerDeleted()
	{
		var stopError = new InvalidOperationException("access denied");
		_service.FailStop = stopError;

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Same(stopError, thrown);
		Assert.Equal(["download", "stop", "start"], _service.Calls);
		Assert.False(File.Exists(_marker));
	}

	[Theory]
	[InlineData(ServiceState.Stopped)]
	[InlineData(ServiceState.NotInstalled)]
	public async Task ServiceNotRunning_NotStoppedNorStarted(ServiceState state)
	{
		_service.State = state;
		_source.FailApply = new InvalidOperationException("locked");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["download", "apply"], _service.Calls);
		Assert.Equal(state, _service.State);
	}

	[Fact]
	public async Task NextTrayStart_AfterInterruptedUpdate_ResumesService()
	{
		await _updates.UpdateAsync(TestContext.Current.CancellationToken);
		_service.Calls.Clear();

		await _updates.ResumeServiceAfterUpdateAsync(TestContext.Current.CancellationToken);

		Assert.Equal(["start"], _service.Calls);
		Assert.False(File.Exists(_marker));
	}

	[Theory]
	[InlineData(false, ServiceState.Stopped)]
	[InlineData(true, ServiceState.Running)]
	public async Task Resume_NoMarkerOrAlreadyRunning_DoesNotStart(bool marker, ServiceState state)
	{
		if (marker)
		{
			await File.WriteAllTextAsync(_marker, string.Empty, TestContext.Current.CancellationToken);
		}

		_service.State = state;

		await _updates.ResumeServiceAfterUpdateAsync(TestContext.Current.CancellationToken);

		Assert.Empty(_service.Calls);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task Checks_AtStartAndEveryHour_ErrorsReportedNotThrown()
	{
		var time = new FakeTimeProvider();
		var reports = Channel.CreateUnbounded<(string? Version, Exception? Error)>();
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var loop = _updates.RunChecksAsync(time, (v, e) => reports.Writer.TryWrite((v, e)), cts.Token);

			Assert.Equal(("1.2.3", null), await NextAsync(reports));
			Assert.Equal("1.2.3", _updates.AvailableVersion);

			time.Advance(TimeSpan.FromMinutes(59));
			Assert.False(reports.Reader.TryRead(out _));
			Assert.Equal(1, _source.Checks);

			_source.FailCheck = new HttpRequestException("rate limited");
			time.Advance(TimeSpan.FromMinutes(1));
			var failed = await NextAsync(reports);
			Assert.Null(failed.Version);
			Assert.IsType<HttpRequestException>(failed.Error);

			_source.FailCheck = null;
			_source.Available = null;
			time.Advance(TimeSpan.FromHours(1));
			Assert.Equal((null, null), await NextAsync(reports));
			Assert.Equal(3, _source.Checks);

			await cts.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
		}
	}

	[Fact]
	public async Task TrayViewModel_UpdateFound_MenuItem_UpdateFailure_ShowsError()
	{
		var vm = new TrayViewModel(_service, _ => Task.FromResult<int?>(0), _updates);
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var checks = vm.RunUpdateChecksAsync(new FakeTimeProvider(), cts.Token);

			Assert.True(vm.IsUpdateAvailable);
			Assert.Equal("Update to v1.2.3", vm.UpdateText);

			_source.FailDownload = new HttpRequestException("offline");
			await vm.UpdateCommand.ExecuteAsync(null);
			Assert.Equal("offline", vm.Error);
			Assert.Equal(ServiceState.Running, vm.State);

			await cts.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks);
		}
	}

	[Fact]
	public async Task TrayViewModel_CheckForUpdates_FoundShowsUpdateItem_NoneSaysUpToDate_FailureShowsError()
	{
		_source.Available = null;
		var vm = new TrayViewModel(_service, _ => Task.FromResult<int?>(0), _updates);
		Assert.Equal("Check for updates", vm.CheckUpdatesText);

		await vm.CheckForUpdatesCommand.ExecuteAsync(null);
		Assert.False(vm.IsUpdateAvailable);
		Assert.Equal("Up to date — check again", vm.CheckUpdatesText);
		Assert.Null(vm.Error);

		_source.Available = "1.2.4";
		await vm.CheckForUpdatesCommand.ExecuteAsync(null);
		Assert.True(vm.IsUpdateAvailable);
		Assert.Equal("Update to v1.2.4", vm.UpdateText);
		Assert.Equal("Check for updates", vm.CheckUpdatesText);

		_source.FailCheck = new HttpRequestException("offline");
		await vm.CheckForUpdatesCommand.ExecuteAsync(null);
		Assert.Equal("Update check failed: offline", vm.Error);
		Assert.True(vm.IsUpdateAvailable);
	}

	[Fact]
	public async Task TrayViewModel_CheckFails_ErrorShown_NoUpdateItem()
	{
		_source.FailCheck = new HttpRequestException("rate limited");
		var vm = new TrayViewModel(_service, _ => Task.FromResult<int?>(0), _updates);
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var checks = vm.RunUpdateChecksAsync(new FakeTimeProvider(), cts.Token);

			Assert.False(vm.IsUpdateAvailable);
			Assert.Equal("Update check failed: rate limited", vm.Error);

			await cts.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks);
		}
	}

	[Theory]
	[InlineData(true, ServiceState.Stopped, "sync start")]
	[InlineData(false, ServiceState.Stopped, "sync")]
	[InlineData(true, ServiceState.Running, "")]
	[InlineData(true, ServiceState.NotInstalled, "")]
	public async Task AfterUpdateHook_SyncsAStoppedService_StartsOnlyWhenTheUpdateStoppedIt(bool marker, ServiceState state, string calls)
	{
		if (marker)
		{
			await File.WriteAllTextAsync(_marker, string.Empty, TestContext.Current.CancellationToken);
		}

		_service.State = state;

		VelopackHooks.AfterUpdate(_service, _marker, Sync);

		Assert.Equal(calls, string.Join(' ', _service.Calls));
		Assert.Equal(marker, File.Exists(_marker));
	}

	[Fact]
	public async Task AfterUpdateHook_OldLayout_NotSynced_StartedFromTheAppFolderAsBefore()
	{
		await File.WriteAllTextAsync(_marker, string.Empty, TestContext.Current.CancellationToken);
		_service.State = ServiceState.Stopped;
		_service.BinaryPathName = OldLayout;

		VelopackHooks.AfterUpdate(_service, _marker, Sync);

		Assert.Equal(["start"], _service.Calls);
	}

	[Fact]
	public async Task AfterUpdateHook_SyncFails_StillStarted()
	{
		await File.WriteAllTextAsync(_marker, string.Empty, TestContext.Current.CancellationToken);
		_service.State = ServiceState.Stopped;

		VelopackHooks.AfterUpdate(_service, _marker, () => throw new IOException("in use"));

		Assert.Equal(["start"], _service.Calls);
	}

	[Fact]
	public async Task AfterUpdateHook_StartFails_DoesNotThrow()
	{
		await File.WriteAllTextAsync(_marker, string.Empty, TestContext.Current.CancellationToken);
		_service.State = ServiceState.Stopped;
		_service.FailStart = new InvalidOperationException("denied");

		VelopackHooks.AfterUpdate(_service, _marker, Sync);

		Assert.Equal(["sync", "start"], _service.Calls);
	}

	[Theory]
	[InlineData(ServiceState.Running, "stop sync start")]
	[InlineData(ServiceState.Starting, "stop sync start")]
	[InlineData(ServiceState.Stopped, "sync")]
	[InlineData(ServiceState.NotInstalled, "")]
	public void AfterInstallHook_StopsSyncsAndRestoresTheState(ServiceState state, string calls)
	{
		_service.State = state;

		VelopackHooks.AfterInstall(_service, Sync, _marker);

		Assert.Equal(calls, string.Join(' ', _service.Calls));
	}

	[Theory]
	[InlineData(ServiceState.Running, true)]
	[InlineData(ServiceState.Stopped, false)]
	public void AfterInstallHook_MarkerWhileStoppedForTheCopy_GoneOnceStartedAgain(ServiceState state, bool marker)
	{
		_service.State = state;
		var seen = false;

		VelopackHooks.AfterInstall(_service, () => seen = File.Exists(_marker), _marker);

		Assert.Equal(marker, seen);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public void AfterInstallHook_StopFails_MarkerLeftForTheTrayToResolve()
	{
		_service.FailStop = new InvalidOperationException("denied");

		VelopackHooks.AfterInstall(_service, Sync, _marker);

		Assert.True(File.Exists(_marker));
	}

	[Fact]
	public void AfterInstallHook_OldLayout_LeftAlone()
	{
		_service.BinaryPathName = OldLayout;

		VelopackHooks.AfterInstall(_service, Sync, _marker);

		Assert.Empty(_service.Calls);
		Assert.Equal(ServiceState.Running, _service.State);
	}

	[Fact]
	public void AfterInstallHook_SyncFails_StartedAgain_DoesNotThrow()
	{
		VelopackHooks.AfterInstall(
			_service,
			() =>
			{
				_service.Calls.Add("sync");
				throw new IOException("in use");
			},
			_marker);

		Assert.Equal(["stop", "sync", "start"], _service.Calls);
		Assert.Equal(ServiceState.Running, _service.State);
	}

	[Fact]
	public void AfterInstallHook_StopFails_NotSynced_DoesNotThrow()
	{
		_service.FailStop = new InvalidOperationException("denied");

		VelopackHooks.AfterInstall(_service, Sync, _marker);

		Assert.Equal(["stop"], _service.Calls);
	}

	[Theory]
	[InlineData(ServiceState.Running, true)]
	[InlineData(ServiceState.Stopped, true)]
	[InlineData(ServiceState.NotInstalled, false)]
	public void BeforeUninstallHook_ElevatedUninstallWhenInstalled_RemovesAutoStart(ServiceState state, bool elevated)
	{
		_service.State = state;
		var commands = new List<string>();
		var autoStart = new FakeAutoStart { IsEnabled = true };

		VelopackHooks.BeforeUninstall(
			_service,
			command =>
			{
				commands.Add(command);
				return Task.FromResult<int?>(0);
			},
			autoStart);

		Assert.Equal(elevated ? ["uninstall"] : [], commands);
		Assert.False(autoStart.IsEnabled);
	}

	[Fact]
	public void BeforeUninstallHook_ElevationFails_DoesNotThrow()
	{
		VelopackHooks.BeforeUninstall(_service, _ => throw new InvalidOperationException("no UAC"), new FakeAutoStart());
	}

	public void Dispose() => File.Delete(_marker);

	private static async Task<T> NextAsync<T>(Channel<T> channel) =>
		await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

	private void Sync() => _service.Calls.Add("sync");
}
