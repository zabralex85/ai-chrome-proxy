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
	public void PrepareDataDirectory_ControlUserOwnedFolder_Allowed()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "mine"));
		Directory.CreateDirectory(dataDir.Root);

		ServiceSetup.PrepareDataDirectory(dataDir, Other, Current);

		Assert.True(Directory.Exists(dataDir.Logs));
	}

	[Fact]
	public void PrepareDataDirectory_ExistingChildren_KeepTheirExactDacl()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "children"));
		Directory.CreateDirectory(dataDir.Logs);
		var file = Path.Combine(dataDir.Root, "user.txt");
		File.WriteAllText(file, "x");
		var oldLog = Path.Combine(dataDir.Logs, "old.log");
		File.WriteAllText(oldLog, "x");
		string[] paths = [file, dataDir.Logs, oldLog];
		var before = paths.Select(RawDacl.Sddl).ToArray();

		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);

		Assert.Equal(before, paths.Select(RawDacl.Sddl).ToArray());
	}

	[Fact]
	public void PrepareDataDirectory_Twice_RootDaclUnchanged()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "twice"));
		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);
		var first = RawDacl.Sddl(dataDir.Root);

		ServiceSetup.PrepareDataDirectory(dataDir, Current, Current);

		Assert.Equal(first, RawDacl.Sddl(dataDir.Root));
	}

	[Fact]
	public void PrepareDataDirectory_RootRuleIsModifyContainerAndObjectInherit_LogsInheritsIt()
	{
		var dataDir = new DataDirectory(Path.Combine(_temp, "rule"));
		Directory.CreateDirectory(dataDir.Root);
		var account = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

		ServiceSetup.PrepareDataDirectory(dataDir, account, Current);

		var rule = Rules(dataDir.Root, inherited: false).Single(r => r.IdentityReference == account);
		Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, rule.FileSystemRights);
		Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
		Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
		Assert.Contains(Rules(dataDir.Logs, inherited: true), r => r.IdentityReference == account && r.IsInherited && r.FileSystemRights.HasFlag(FileSystemRights.Modify));
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

	private static SecurityIdentifier? OwnerOf(string path) =>
		new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

	private static void Junction(string link, string target)
	{
		using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
		process.WaitForExit();
		Assert.Equal(0, process.ExitCode);
	}

	private static IEnumerable<FileSystemAccessRule> Rules(string path, bool inherited) =>
		new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, inherited, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
}
