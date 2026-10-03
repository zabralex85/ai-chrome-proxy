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

	private static readonly SecurityIdentifier Owner = WindowsIdentity.GetCurrent().Owner!;

	private readonly string _temp = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));
	private readonly string _package;
	private readonly DataDirectory _dataDir;

	public ServerDirectoryTests()
	{
		_package = Path.Combine(_temp, "current", "server");
		_dataDir = new DataDirectory(Path.Combine(_temp, "data"));
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
		ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1);

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

		ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1);

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
			Assert.ThrowsAny<IOException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1));

			Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		}

		ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1);

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_CopyFails_OldKept()
	{
		WriteServer("v1");
		using (new FileStream(Path.Combine(_package, Exe), FileMode.Open, FileAccess.Read, FileShare.None))
		{
			Assert.ThrowsAny<IOException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1));
		}

		Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
	}

	[Fact]
	public void Sync_PackageWithoutServer_Refused_NothingTouched()
	{
		WriteServer("v1");
		File.Delete(Path.Combine(_package, Exe));

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1));

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

		ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1);

		Assert.Equal("v2", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
		AssertOnlyServer();
	}

	[Fact]
	public void Sync_CrashBetweenTheRenames_LastCopyRestoredFirst()
	{
		WriteTree(_dataDir.Server + ".old", "v1");
		File.Delete(Path.Combine(_package, Exe));

		Assert.Throws<InvalidOperationException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1));

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
			ServiceSetup.SyncServerDirectory(_package, _dataDir, Owner, attempts: 1);

			Assert.False(Directory.Exists(Path.Combine(_dataDir.Server, "link")));
			Assert.True(File.Exists(Path.Combine(_dataDir.Server, Exe)));
		}
		finally
		{
			Directory.Delete(link);
		}
	}

	[Fact]
	public void Sync_StagingNotOwnedByThisProcess_Refused()
	{
		WriteServer("v1");

		// A group this user is in: the folder is readable (as with the real owner), but its owner is this user, not the group.
		var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.SyncServerDirectory(_package, _dataDir, users, attempts: 1));

		Assert.Contains("created by another user", ex.Message, StringComparison.Ordinal);
		Assert.Equal("v1", File.ReadAllText(Path.Combine(_dataDir.Server, Exe)));
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
	[InlineData(true, false, "stop sync configure start")]
	[InlineData(false, false, "stop sync configure")]
	[InlineData(false, true, "stop sync configure start")]
	public void InstallSequence_StartsWhenItRanOrIsNew(bool wasRunning, bool created, string calls)
	{
		var log = new List<string>();

		ServiceSetup.RunInstallSequence(
			() => Log(log, "stop", wasRunning),
			() => log.Add("sync"),
			() => Log(log, "configure", created),
			() => log.Add("start"));

		Assert.Equal(calls, string.Join(' ', log));
	}

	[Theory]
	[InlineData(true, "stop sync start")]
	[InlineData(false, "stop sync")]
	public void InstallSequence_SyncFails_NotReconfigured_RanBeforeStartedAgain(bool wasRunning, string calls)
	{
		var log = new List<string>();
		var error = new IOException("in use");

		var thrown = Assert.Throws<IOException>(() => ServiceSetup.RunInstallSequence(
			() => Log(log, "stop", wasRunning),
			() =>
			{
				log.Add("sync");
				throw error;
			},
			() => Log(log, "configure", false),
			() =>
			{
				log.Add("start");
				throw new InvalidOperationException("start failed too");
			}));

		Assert.Same(error, thrown);
		Assert.Equal(calls, string.Join(' ', log));
	}

	private static bool Log(List<string> log, string call, bool result)
	{
		log.Add(call);
		return result;
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
