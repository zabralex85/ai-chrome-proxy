using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class ServiceSetupSecurityTests : IDisposable
{
	private static readonly SecurityIdentifier Other = new("S-1-5-21-1000000000-2000000000-3000000000-1001");
	private static readonly SecurityIdentifier Current = WindowsIdentity.GetCurrent().User!;

	private readonly string _temp = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));

	public ServiceSetupSecurityTests() => Directory.CreateDirectory(_temp);

	public void Dispose() => Directory.Delete(_temp, recursive: true);

	[Fact]
	public void IsTrustedOwner_AdminsSystemAndListedOnly()
	{
		Assert.True(ServiceSetup.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
		Assert.True(ServiceSetup.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
		Assert.True(ServiceSetup.IsTrustedOwner(new SecurityIdentifier(ServiceSetup.TrustedInstallerSid)));
		Assert.True(ServiceSetup.IsTrustedOwner(Other, Other));
		Assert.False(ServiceSetup.IsTrustedOwner(Other, Current));
		Assert.False(ServiceSetup.IsTrustedOwner(null, Current));
	}

	[Fact]
	public void EnsureDataDirectorySafe_OwnedByControlUser_Allowed()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "ok"));
		Directory.CreateDirectory(dataDir.Logs);

		ServiceSetup.EnsureDataDirectorySafe(dataDir, Other, Current);
	}

	[Fact]
	public void EnsureDataDirectorySafe_OwnedByNobodyTrusted_Refused()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "foreign"));
		Directory.CreateDirectory(dataDir.Logs);

		// Cannot chown without admin: skip when the folder's real owner (the user, or Administrators when elevated) is trusted anyway.
		Assert.SkipWhen(ServiceSetup.IsTrustedOwner(OwnerOf(dataDir.Root)), "The temp folder owner is trusted (running elevated), so a foreign owner cannot be simulated.");

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.EnsureDataDirectorySafe(dataDir, Other, Other));
		Assert.Contains("created by another user", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EnsureOwnersTrusted_FirstUntrustedEntry_Refused()
	{
		var owners = new Dictionary<string, SecurityIdentifier?>
		{
			["appsettings.json"] = Current,
			["logs"] = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
			["appsettings.json.tmp"] = Other,
			["x"] = null,
		};

		ServiceSetup.EnsureOwnersTrusted(owners.Keys.Take(2), p => owners[p], Current);
		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.EnsureOwnersTrusted(owners.Keys, p => owners[p], Current));
		Assert.Equal("appsettings.json.tmp was created by another user; delete it and retry.", ex.Message);
		Assert.Throws<InvalidOperationException>(() => ServiceSetup.EnsureOwnersTrusted(["x"], p => owners[p], Current));
	}

	[Fact]
	public void EnsureDataDirectorySafe_EntriesOwnedByTrustedUser_Allowed()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "entries"));
		Directory.CreateDirectory(dataDir.Logs);
		File.WriteAllText(dataDir.SettingsFile + ".tmp", "x");
		File.WriteAllText(Path.Combine(dataDir.Logs, "server-20260101.clef"), "x");

		ServiceSetup.EnsureDataDirectorySafe(dataDir, Current, Current);
	}

	[Fact]
	public void OwnerOf_Link_NotFollowed()
	{
		var target = Directory.CreateDirectory(Path.Combine(_temp, "gone")).FullName;
		var link = Path.Combine(_temp, "dangling");
		Junction(link, target);
		Directory.Delete(target);
		try
		{
			Assert.Equal(OwnerOf(_temp), DataDirectoryGuard.OwnerOf(link));
		}
		finally
		{
			Directory.Delete(link);
		}
	}

	[Fact]
	public void PrepareDataDirectory_RootIsJunction_Refused_NothingCreatedInTarget()
	{
		var target = Directory.CreateDirectory(Path.Combine(_temp, "target")).FullName;
		var root = Path.Combine(_temp, "root");
		Junction(root, target);
		try
		{
			var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareDataDirectory(new DataDirectory(root), Current, Current));

			Assert.Contains("is a link; delete it and retry", ex.Message, StringComparison.Ordinal);
			Assert.Empty(Directory.GetFileSystemEntries(target));
		}
		finally
		{
			Directory.Delete(root);
		}
	}

	[Fact]
	public void PrepareDataDirectory_LogsIsJunction_Refused()
	{
		var target = Directory.CreateDirectory(Path.Combine(_temp, "target2")).FullName;
		var dataDir = new DataDirectory(Path.Combine(_temp, "root2"));
		Directory.CreateDirectory(dataDir.Root);
		Junction(dataDir.Logs, target);
		try
		{
			Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareDataDirectory(dataDir, Current, Current));
		}
		finally
		{
			Directory.Delete(dataDir.Logs);
		}
	}

	[Fact]
	public void PrepareDataDirectory_ForeignOwnedEntry_Refused_LogsNotCreated()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "foreign-entry"));
		Directory.CreateDirectory(dataDir.Root);
		File.WriteAllText(dataDir.SettingsFile, "{}");

		var ex = Assert.Throws<InvalidOperationException>(
			() => ServiceSetup.PrepareDataDirectory(dataDir, Current, Current, path => path == dataDir.SettingsFile ? Other : Current));

		Assert.Equal($"{dataDir.SettingsFile} was created by another user; delete it and retry.", ex.Message);
		Assert.False(Directory.Exists(dataDir.Logs));
	}

	[Fact]
	public void PrepareDataDirectory_ForeignOwnedRoot_Refused_DaclUntouched()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "foreign-root"));
		Directory.CreateDirectory(dataDir.Root);
		var before = RawDacl.Sddl(dataDir.Root);

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareDataDirectory(dataDir, Current, Current, _ => Other));

		Assert.Equal($"{dataDir.Root} was created by another user; delete it and retry.", ex.Message);
		Assert.Equal(before, RawDacl.Sddl(dataDir.Root));
		Assert.False(Directory.Exists(dataDir.Logs));
	}

	[Fact]
	public void PrepareDataDirectory_ControlUserOwnedFolder_Allowed()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "mine"));
		Directory.CreateDirectory(dataDir.Root);

		// Authenticated Users stands in for another account: the test user stays able to work in (and delete) the folder.
		ServiceSetup.PrepareDataDirectory(dataDir, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), Current);

		Assert.True(Directory.Exists(dataDir.Logs));
	}

	[Fact]
	public void PrepareDataDirectory_ExistingFiles_KeepTheirExactDacl()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "children"));
		Directory.CreateDirectory(dataDir.Logs);
		var file = Path.Combine(dataDir.Root, "user.txt");
		File.WriteAllText(file, "x");
		var oldLog = Path.Combine(dataDir.Logs, "old.log");
		File.WriteAllText(oldLog, "x");
		string[] paths = [file, oldLog];
		var before = paths.Select(RawDacl.Sddl).ToArray();

		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);

		Assert.Equal(before, paths.Select(RawDacl.Sddl).ToArray());
	}

	[Fact]
	public void PrepareDataDirectory_Twice_StoredDaclIsTheExpectedOne()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "twice"));
		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);

		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);

		// Stored exactly as computed, so the second run's comparison finds nothing to write.
		Assert.Equal(ServiceSetup.DataDirectoryDacl(Current), RawDacl.Sddl(dataDir.Root));
		Assert.Equal(ServiceSetup.DataDirectoryDacl(Current), RawDacl.Sddl(dataDir.Logs));
	}

	[Fact]
	public void DataDirectoryDacl_Protected_SystemAdminsFull_AccountModify_NoBroadGroups()
	{
		var descriptor = new RawSecurityDescriptor(ServiceSetup.DataDirectoryDacl(Other));
		var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();

		Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
		Assert.All(aces, a => Assert.Equal(AceType.AccessAllowed, a.AceType));
		Assert.All(aces, a => Assert.Equal(AceFlags.ContainerInherit | AceFlags.ObjectInherit, a.AceFlags));
		Assert.Equal(
			[
				(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), (int)FileSystemRights.FullControl),
				(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), (int)FileSystemRights.FullControl),
				(Other, (int)(FileSystemRights.Modify | FileSystemRights.Synchronize)),
			],
			aces.Select(a => (a.SecurityIdentifier, a.AccessMask)));
	}

	[Fact]
	public void PrepareDataDirectory_RootAndLogsProtected_NewFilesInheritOnlyTheSet()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "protected"));
		Directory.CreateDirectory(dataDir.Logs);

		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);
		File.WriteAllText(dataDir.SettingsFile, "{}");

		Assert.Equal(ServiceSetup.DataDirectoryDacl(Current), RawDacl.Sddl(dataDir.Root));
		Assert.Equal(ServiceSetup.DataDirectoryDacl(Current), RawDacl.Sddl(dataDir.Logs));
		var inherited = new RawSecurityDescriptor(RawDacl.Sddl(dataDir.SettingsFile)).DiscretionaryAcl!.Cast<CommonAce>().ToList();
		Assert.Equal(3, inherited.Count);
		Assert.All(inherited, a => Assert.True(a.IsInherited));
		Assert.DoesNotContain(inherited, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
			|| a.SecurityIdentifier.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
			|| a.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid));
	}

	[Fact]
	public void PrepareSettingsDirectory_ForeignOwnedEntry_Refused()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "settings-entry"));
		Directory.CreateDirectory(dataDir.Root);
		File.WriteAllText(dataDir.SettingsFile, "{}");

		var ex = Assert.Throws<InvalidOperationException>(
			() => ServiceSetup.PrepareSettingsDirectory(dataDir, Current, path => path == dataDir.SettingsFile ? Other : Current));

		Assert.Equal($"{dataDir.SettingsFile} was created by another user; delete it and retry.", ex.Message);
	}

	[Fact]
	public void PrepareSettingsDirectory_ForeignOwnedRoot_Refused_DaclUntouched()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "settings-root"));
		Directory.CreateDirectory(dataDir.Root);
		var before = RawDacl.Sddl(dataDir.Root);

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareSettingsDirectory(dataDir, Current, _ => Other));

		Assert.Equal($"{dataDir.Root} was created by another user; delete it and retry.", ex.Message);
		Assert.Equal(before, RawDacl.Sddl(dataDir.Root));
	}

	[Fact]
	public void PrepareSettingsDirectory_AdminOwnedUnprotectedRoot_Refused()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "settings-unprotected"));
		Directory.CreateDirectory(dataDir.Root);

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareSettingsDirectory(dataDir, Current, OwnedByAdministrators(dataDir.Root)));

		Assert.Equal(
			$"{dataDir.Root} is not protected. Ask an administrator to delete it (back up appsettings.json and the logs folder first: they are deleted with it), then retry.",
			ex.Message);
	}

	[Fact]
	public void PrepareSettingsDirectory_ElevatedTray_AdminOwnedUnprotectedRoot_Protected()
	{
		// An elevated process (UAC off, built-in Administrator, CI) owns what it creates as Administrators, so that folder is its own.
		var dataDir = new DataDirectory(Path.Combine(_temp, "settings-elevated"));
		Directory.CreateDirectory(dataDir.Root);
		var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

		ServiceSetup.PrepareSettingsDirectory(dataDir, Current, OwnedByAdministrators(dataDir.Root), tokenOwner: administrators);

		Assert.Equal(ServiceSetup.DataDirectoryDacl(Current), RawDacl.Sddl(dataDir.Root));
	}

	[Fact]
	public void PrepareSettingsDirectory_AdminOwnedProtectedRoot_LeftAsItIs()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "settings-installed"));

		// As an install leaves it; Authenticated Users stands in for the service account so the DACL differs from the user's own.
		var account = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
		ServiceSetup.PrepareDataDirectory(dataDir, account, Current);

		ServiceSetup.PrepareSettingsDirectory(dataDir, Current, OwnedByAdministrators(dataDir.Root));

		Assert.Equal(ServiceSetup.DataDirectoryDacl(account), RawDacl.Sddl(dataDir.Root));
	}

	[Fact]
	public void PrepareSettingsDirectory_RootIsJunction_Refused()
	{
		var target = Directory.CreateDirectory(Path.Combine(_temp, "settings-target")).FullName;
		var root = Path.Combine(_temp, "settings-link");
		Junction(root, target);
		try
		{
			// Whoever owns the link itself.
			Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareSettingsDirectory(new DataDirectory(root), Current, _ => Current));
			Assert.Throws<InvalidOperationException>(() => ServiceSetup.PrepareSettingsDirectory(new DataDirectory(root), Current, OwnedByAdministrators(root)));
			Assert.Empty(Directory.GetFileSystemEntries(target));
		}
		finally
		{
			Directory.Delete(root);
		}
	}

	[Fact]
	public void SettingsFileDacl_Protected_NotInherited_SystemAdminsFull_UserModify()
	{
		var descriptor = new RawSecurityDescriptor(ServiceSetup.SettingsFileDacl(Other));
		var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();

		Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
		Assert.All(aces, a => Assert.Equal(AceFlags.None, a.AceFlags));
		Assert.Equal(
			[
				(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), (int)FileSystemRights.FullControl),
				(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), (int)FileSystemRights.FullControl),
				(Other, (int)(FileSystemRights.Modify | FileSystemRights.Synchronize)),
			],
			aces.Select(a => (a.SecurityIdentifier, a.AccessMask)));
	}

	[Fact]
	public void EnsureServiceAccountIsControlUser_Different_Refused_Same_Allowed()
	{
		ServiceSetup.EnsureServiceAccountIsControlUser(Current, Current);

		var ex = Assert.Throws<InvalidOperationException>(() => ServiceSetup.EnsureServiceAccountIsControlUser(Other, Current));
		Assert.Contains("same user", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RunUninstallSequence_DeleteThenStopThenWait_StopFailureIgnored()
	{
		var calls = new List<string>();

		ServiceSetup.RunUninstallSequence(
			() => calls.Add("delete"),
			() =>
			{
				calls.Add("stop");
				throw new InvalidOperationException("1062");
			},
			() => calls.Add("wait"));

		Assert.Equal(["delete", "stop", "wait"], calls);
	}

	[Fact]
	public void RunUninstallSequence_DeleteFails_NothingElseRuns()
	{
		var calls = new List<string>();

		Assert.Throws<InvalidOperationException>(() => ServiceSetup.RunUninstallSequence(
			() => throw new InvalidOperationException("denied"),
			() => calls.Add("stop"),
			() => calls.Add("wait")));

		Assert.Empty(calls);
	}

	[Fact]
	public async Task InstallViewModel_Failure_ShowsInnerReason()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled)
		{
			FailInstall = new InvalidOperationException("Cannot start service", new InvalidOperationException("Logon failure (1069)")),
		};
		var vm = new InstallViewModel(service, @"HOME\jane") { Password = "x" };

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal("Logon failure (1069)", vm.Error);
	}

	[Fact]
	public void DataDirectoryGuard_HeldHandle_BlocksRenameAndDelete()
	{
		var root = Path.Combine(_temp, "held");
		using (DataDirectoryGuard.Acquire(root))
		{
			Assert.ThrowsAny<IOException>(() => Directory.Move(root, root + "-moved"));
			Assert.ThrowsAny<IOException>(() => Directory.Delete(root));
		}

		Directory.Move(root, root + "-moved");
	}

	[Fact]
	public void DataDirectoryGuard_Junction_Refused()
	{
		var target = Directory.CreateDirectory(Path.Combine(_temp, "gt")).FullName;
		var root = Path.Combine(_temp, "groot");
		Junction(root, target);
		try
		{
			var ex = Assert.Throws<InvalidOperationException>(() => DataDirectoryGuard.Acquire(root));
			Assert.Contains("is a link", ex.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(root);
		}
	}

	[Fact]
	public void RunUninstall_Success_Zero()
	{
		Assert.Equal(0, AdminCommand.RunUninstall(new FakeServiceControl()));
	}

	[Fact]
	public void RunUninstall_Failure_ExitsWithWin32Code()
	{
		Assert.Equal(5, AdminCommand.RunUninstall(new FakeServiceControl { FailUninstall = new Win32Exception(5) }));
		Assert.Equal(1060, AdminCommand.RunUninstall(new FakeServiceControl { FailUninstall = new InvalidOperationException("x", new Win32Exception(1060)) }));
	}

	[Fact]
	public void ExitCodeFor_NoWin32Code_One()
	{
		Assert.Equal(1, AdminCommand.ExitCodeFor(new InvalidOperationException("x")));
		Assert.Equal(1, AdminCommand.ExitCodeFor(new InvalidOperationException("x", new IOException("y"))));
		Assert.Equal(1, AdminCommand.ExitCodeFor(new Win32Exception(0)));
	}

	internal static void Junction(string link, string target)
	{
		using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!)
		{
			process.WaitForExit();
			Assert.Equal(0, process.ExitCode);
		}
	}

	/// <summary>The root owned by Administrators (as after an elevated install), everything else by the current user.</summary>
	private static Func<string, SecurityIdentifier?> OwnedByAdministrators(string root) =>
		path => path == root ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) : Current;

	private static SecurityIdentifier? OwnerOf(string path) =>
		new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
}
