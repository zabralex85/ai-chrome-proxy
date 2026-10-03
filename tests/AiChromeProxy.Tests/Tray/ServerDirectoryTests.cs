using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

/// <summary>The service's copy of the Server in <c>&lt;DataDir&gt;\server</c>: path, migration detection, sync and removal (temp folders only).</summary>
public sealed class ServerDirectoryTests : IDisposable
{
	private const string Exe = "AiChromeProxy.Server.exe";
	private const string ServiceExe = @"C:\ProgramData\AiChromeProxy\server\AiChromeProxy.Server.exe";

	private readonly string _temp = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));
	private readonly string _package;
	private readonly DataDirectory _dataDir;
	private readonly string _marker;

	public ServerDirectoryTests()
	{
		_package = Path.Combine(_temp, "current", "server");
		_dataDir = new DataDirectory(Path.Combine(_temp, "data"));
		_marker = Path.Combine(_temp, "update-pending");
		Directory.CreateDirectory(_dataDir.Logs);
		File.WriteAllText(_dataDir.SettingsFile, "{}");
		WritePackage("v2");
	}

	public void Dispose() => Directory.Delete(_temp, recursive: true);

	[Fact]
	public void ServiceExecutable_DefaultDataDirectory_ServerFolder()
	{
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

		Assert.Equal(Path.Combine(programData, "AiChromeProxy", "server", Exe), ServiceSetup.ServiceExecutable);
		Assert.Equal(Path.Combine(_dataDir.Root, "server"), _dataDir.Server);
	}

	[Theory]
	[InlineData("\"" + ServiceExe + "\"", true)]
	[InlineData(ServiceExe, true)]
	[InlineData("\"C:\\PROGRAMDATA\\aichromeproxy\\SERVER\\AiChromeProxy.Server.exe\"", true)]
	[InlineData("\"C:\\Users\\Jane Doe\\AppData\\Local\\AiChromeProxy\\current\\server\\AiChromeProxy.Server.exe\"", false)]
	[InlineData("\"C:\\ProgramData\\AiChromeProxy\\server\\..\\other\\AiChromeProxy.Server.exe\"", false)]
	[InlineData("\"C:\\ProgramData\\AiChromeProxy\\server2\\AiChromeProxy.Server.exe\"", false)]
	[InlineData("\"", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void RunsFrom_OnlyTheServiceCopy(string? binaryPathName, bool expected)
	{
		Assert.Equal(expected, ServiceSetup.RunsFrom(binaryPathName, ServiceExe));
	}

	[Fact]
	public void Sync_FirstTime_CopiesPackage_InheritsDataDirectoryPermissions()
	{
		ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1);

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, "wwwroot", "index.html")));
		AssertOnlyServer();
		foreach (var path in new[] { _dataDir.Server, Path.Combine(_dataDir.Server, "wwwroot", "index.html") })
		{
			var security = new FileInfo(path).Attributes.HasFlag(FileAttributes.Directory)
				? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
				: new FileInfo(path).GetAccessControl();
			Assert.False(security.AreAccessRulesProtected);
			Assert.Empty(security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)));
			Assert.NotEmpty(security.GetAccessRules(includeExplicit: false, includeInherited: true, typeof(SecurityIdentifier)));
		}
	}

	[Fact]
	public void Sync_Existing_ReplacedWhole_FilesNoLongerShippedRemoved()
	{
		WriteServer("v1", "removed.dll");

		ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1);

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		Assert.False(File.Exists(Path.Combine(_dataDir.Server, "removed.dll")));
		Assert.True(File.Exists(_dataDir.SettingsFile));
		Assert.True(Directory.Exists(_dataDir.Logs));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_ServerInUse_OldKept_NextSyncCleansUpAndSucceeds()
	{
		WriteServer("v1");
		using (new FileStream(Path.Combine(_dataDir.Server, Exe), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Assert.ThrowsAny<IOException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1));

			Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		}

		ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1);

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public async Task Sync_ServerBrieflyInUse_RetriedUntilFree()
	{
		WriteServer("v1");
		var handle = new FileStream(Path.Combine(_dataDir.Server, Exe), FileMode.Open, FileAccess.Read, FileShare.Read);
		var release = Task.Delay(TimeSpan.FromMilliseconds(700), TestContext.Current.CancellationToken).ContinueWith(_ => handle.Dispose(), TaskScheduler.Default);

		ServiceSetup.SyncServerDirectory(_package, _dataDir);
		await release;

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_CopyFails_OldKept()
	{
		WriteServer("v1");
		using (new FileStream(Path.Combine(_package, Exe), FileMode.Open, FileAccess.Read, FileShare.None))
		{
			Assert.ThrowsAny<IOException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1));
		}

		Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
	}

	[Fact]
	public void Sync_PackageWithoutServer_Refused_NothingTouched()
	{
		WriteServer("v1");
		File.Delete(Path.Combine(_package, Exe));

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1));

		Assert.Contains(Exe, ex.Message, StringComparison.Ordinal);
		Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_StaleStagingAndOldFromACrash_Cleaned()
	{
		WriteServer("v1");
		WriteTree(_dataDir.Server + ".new", "half");
		WriteTree(_dataDir.Server + ".old", "v0");

		ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1);

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_CrashBetweenTheRenames_LastCopyRestoredFirst()
	{
		WriteTree(_dataDir.Server + ".old", "v1");
		File.Delete(Path.Combine(_package, Exe));

		Assert.Throws<InvalidOperationException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1));

		Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_LinksInPackage_NotFollowed()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
		var link = Path.Combine(_package, "link");
		ServiceSetupSecurityTests.Junction(link, outside);
		try
		{
			ServiceSetup.SyncServerDirectory(_package, _dataDir, attempts: 1);

			Assert.False(Directory.Exists(Path.Combine(_dataDir.Server, "link")));
			Assert.True(File.Exists(Path.Combine(_dataDir.Server, Exe)));
		}
		finally
		{
			Directory.Delete(link);
		}
	}

	[Fact]
	public void CleanUpLeftovers_RestoresAnInterruptedSwap_DeletesStaging()
	{
		WriteTree(_dataDir.Server + ".old", "v1");
		WriteTree(_dataDir.Server + ".new", "half");

		ServiceSetup.CleanUpServerLeftovers(_dataDir, attempts: 1);

		Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();

		// Nothing left over: nothing to do.
		ServiceSetup.CleanUpServerLeftovers(_dataDir, attempts: 1);
		ServiceSetup.CleanUpServerLeftovers(new DataDirectory(Path.Combine(_temp, "missing")), attempts: 1);
	}

	[Fact]
	public void EnsureServerCopied_Present_Accepted()
	{
		WriteServer("v1");

		ServiceSetup.EnsureServerCopied(_dataDir);
	}

	[Fact]
	public void EnsureServerCopied_Missing_ClearMessage()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.EnsureServerCopied(_dataDir));

		Assert.Contains(Path.Combine(_dataDir.Server, Exe), ex.Message, StringComparison.Ordinal);
		Assert.Contains("Install service", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EnsureServerCopied_ServerFolderIsALink_Refused()
	{
		var outside = Path.Combine(_temp, "outside");
		WriteTree(outside, "v1");
		ServiceSetupSecurityTests.Junction(_dataDir.Server, outside);
		try
		{
			var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.EnsureServerCopied(_dataDir));

			Assert.Contains("is a link", ex.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(_dataDir.Server);
		}
	}

	[Fact]
	public void DeleteServerDirectory_RemovesCopyAndLeftovers_KeepsSettingsAndLogs()
	{
		WriteServer("v1");
		WriteTree(_dataDir.Server + ".new", "half");
		WriteTree(_dataDir.Server + ".old", "v0");

		ServiceSetup.DeleteServerDirectory(_dataDir, attempts: 1);

		Assert.Equal(new[] { _dataDir.SettingsFile, _dataDir.Logs }.Order(), Directory.GetFileSystemEntries(_dataDir.Root).Order());

		// Nothing to delete: no error.
		ServiceSetup.DeleteServerDirectory(_dataDir, attempts: 1);
	}

	[Fact]
	public void DeleteServerDirectory_InUse_ClearError()
	{
		WriteServer("v1");
		using (new FileStream(Path.Combine(_dataDir.Server, Exe), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var ex = Assert.Throws<IOException>(() => ServiceSetup.DeleteServerDirectory(_dataDir, attempts: 1));

			Assert.Contains(_dataDir.Server, ex.Message, StringComparison.Ordinal);
			Assert.Contains("delete it", ex.Message, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData(ServiceState.Running, "stop sync elevate:install start")]
	[InlineData(ServiceState.Starting, "stop sync elevate:install start")]
	[InlineData(ServiceState.Stopped, "sync elevate:install start")]
	[InlineData(ServiceState.NotInstalled, "sync elevate:install start")]
	public async Task AdminInstall_UserStopsAndSyncs_ElevatedPartOnly_ServiceStartedAfterwards(ServiceState state, string calls)
	{
		var service = new FakeServiceControl(state);

		var exitCode = await RunAdminAsync(service, AdminCommand.Install, 0, _marker);

		Assert.Equal(0, exitCode);
		Assert.Equal(calls, string.Join(' ', service.Calls));
		Assert.Equal(ServiceState.Running, service.State);
	}

	[Theory]
	[InlineData(ServiceState.Running, true)]
	[InlineData(ServiceState.Stopped, false)]
	[InlineData(ServiceState.NotInstalled, false)]
	public async Task AdminInstall_MarkerWhileTheServiceIsStoppedForTheCopy_GoneAfterwards(ServiceState state, bool marker)
	{
		var service = new FakeServiceControl(state);
		var seen = new List<bool>();

		await ServiceSetup.RunAdminCommandAsync(
			AdminCommand.Install,
			service,
			() => seen.Add(File.Exists(_marker)),
			() => service.Calls.Add("delete"),
			command =>
			{
				seen.Add(File.Exists(_marker));
				return Elevate(service, command, 0);
			},
			_marker);

		Assert.Equal([marker, marker], seen);
		Assert.False(File.Exists(_marker));
	}

	[Theory]
	[InlineData(null)]
	[InlineData(AdminCommand.Cancelled)]
	[InlineData(5)]
	public async Task AdminInstall_DeclinedOrFailed_StoppedServiceStaysStopped(int? elevatedExitCode)
	{
		var service = new FakeServiceControl(ServiceState.Stopped);

		await RunAdminAsync(service, AdminCommand.Install, elevatedExitCode, _marker);

		Assert.Equal("sync elevate:install", string.Join(' ', service.Calls));
	}

	[Theory]
	[InlineData(null)]
	[InlineData(AdminCommand.Cancelled)]
	[InlineData(5)]
	public async Task AdminInstall_DeclinedOrFailed_ServiceStartedAgain_ExitCodePassedOn(int? elevatedExitCode)
	{
		var service = new FakeServiceControl(ServiceState.Running);

		var exitCode = await RunAdminAsync(service, AdminCommand.Install, elevatedExitCode, _marker);

		Assert.Equal(elevatedExitCode, exitCode);
		Assert.Equal("stop sync elevate:install start", string.Join(' ', service.Calls));
	}

	[Fact]
	public async Task AdminInstall_SyncFails_NotElevated_StartedAgain_ErrorRethrown()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var error = new IOException("in use");

		var thrown = await Assert.ThrowsAsync<IOException>(() => ServiceSetup.RunAdminCommandAsync(
			AdminCommand.Install,
			service,
			() =>
			{
				service.Calls.Add("sync");
				throw error;
			},
			() => service.Calls.Add("delete"),
			command => Elevate(service, command, 0),
			_marker));

		Assert.Same(error, thrown);
		Assert.Equal("stop sync start", string.Join(' ', service.Calls));
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task AdminInstall_ElevationThrows_RestartFailsToo_ElevationErrorRethrown()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var error = new InvalidOperationException("no UAC");

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceSetup.RunAdminCommandAsync(
			AdminCommand.Install,
			service,
			() =>
			{
				service.Calls.Add("sync");
				service.FailStart = new InvalidOperationException("start failed");
			},
			() => service.Calls.Add("delete"),
			_ => throw error,
			_marker));

		Assert.Same(error, thrown);
		Assert.Equal("stop sync start", string.Join(' ', service.Calls));
	}

	[Fact]
	public async Task AdminUninstall_ElevatedRemovesService_ThenUserDeletesServerFolder()
	{
		var service = new FakeServiceControl(ServiceState.Running);

		var exitCode = await RunAdminAsync(service, AdminCommand.Uninstall, 0, _marker);

		Assert.Equal(0, exitCode);
		Assert.Equal("elevate:uninstall delete", string.Join(' ', service.Calls));
	}

	[Theory]
	[InlineData(null)]
	[InlineData(AdminCommand.Cancelled)]
	[InlineData(5)]
	public async Task AdminUninstall_DeclinedOrFailed_ServerFolderKept(int? elevatedExitCode)
	{
		var service = new FakeServiceControl(ServiceState.Running);

		var exitCode = await RunAdminAsync(service, AdminCommand.Uninstall, elevatedExitCode, _marker);

		Assert.Equal(elevatedExitCode, exitCode);
		Assert.Equal("elevate:uninstall", string.Join(' ', service.Calls));
	}

	private static Task<int?> RunAdminAsync(FakeServiceControl service, string command, int? elevatedExitCode, string marker) =>
		ServiceSetup.RunAdminCommandAsync(
			command,
			service,
			() => service.Calls.Add("sync"),
			() => service.Calls.Add("delete"),
			c => Elevate(service, c, elevatedExitCode),
			marker);

	/// <summary>The elevated instance: on success it creates a missing service (stopped) or removes it.</summary>
	private static Task<int?> Elevate(FakeServiceControl service, string command, int? exitCode)
	{
		service.Calls.Add("elevate:" + command);
		if (exitCode == 0)
		{
			service.State = command == AdminCommand.Uninstall ? ServiceState.NotInstalled
				: service.State == ServiceState.NotInstalled ? ServiceState.Stopped : service.State;
		}

		return Task.FromResult(exitCode);
	}

	private static void WriteTree(string root, string version, string? extraFile = null)
	{
		Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
		File.WriteAllText(Path.Combine(root, Exe), version);
		File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), version);
		if (extraFile is not null)
		{
			File.WriteAllText(Path.Combine(root, extraFile), version);
		}
	}

	private void WritePackage(string version) => WriteTree(_package, version);

	private void WriteServer(string version, string? extraFile = null) => WriteTree(_dataDir.Server, version, extraFile);

	private void AssertOnlyServer()
	{
		Assert.True(Directory.Exists(_dataDir.Server));
		Assert.False(Directory.Exists(_dataDir.Server + ".new"));
		Assert.False(Directory.Exists(_dataDir.Server + ".old"));
	}
}
