using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class ServiceSetupTests
{
	// Typical default service DACL: SYSTEM, Administrators, interactive users (query only).
	private const string DefaultServiceSddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)";

	private static readonly SecurityIdentifier User = new("S-1-5-21-1000000000-2000000000-3000000000-1001");

	[Fact]
	public void ServerExecutable_ServerFolderNextToTray_QuotedForTheScm()
	{
		var exe = ServiceSetup.ServerExecutable(@"C:\Users\Jane Doe\AppData\Local\AiChromeProxy\current\");

		Assert.Equal(@"C:\Users\Jane Doe\AppData\Local\AiChromeProxy\current\server\AiChromeProxy.Server.exe", exe);
		Assert.Equal($"\"{exe}\"", ServiceSetup.BinaryPathName(exe));
	}

	[Theory]
	[InlineData(@"HOMESERVER\jane", "HOMESERVER", "jane", @"HOMESERVER\jane")]
	[InlineData(@" CORP\jane.doe ", "CORP", "jane.doe", @"CORP\jane.doe")]
	[InlineData("jane", ".", "jane", @".\jane")]
	public void Account_SplitAndServiceStartName(string account, string domain, string user, string startName)
	{
		Assert.Equal((domain, user), ServiceSetup.SplitAccount(account));
		Assert.Equal(startName, ServiceSetup.ServiceStartName(account));
	}

	[Fact]
	public void Sid_CurrentUser_BothNameForms()
	{
		var current = WindowsIdentity.GetCurrent();
		var bareName = current.Name[(current.Name.IndexOf('\\', StringComparison.Ordinal) + 1)..];

		Assert.Equal(current.User, ServiceSetup.Sid(current.Name));
		if (current.Name.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase))
		{
			Assert.Equal(current.User, ServiceSetup.Sid(bareName));
		}
	}

	[Fact]
	public void GrantUserControl_AddsAllowAceWithStartStopQuery_KeepsOthers()
	{
		var original = new RawSecurityDescriptor(DefaultServiceSddl);

		var granted = new RawSecurityDescriptor(ServiceSetup.GrantUserControl(Binary(original), User), 0);

		var aces = granted.DiscretionaryAcl!.Cast<CommonAce>().ToList();
		Assert.Equal(original.DiscretionaryAcl!.Count + 1, aces.Count);
		var ace = Assert.Single(aces, a => a.SecurityIdentifier == User);
		Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
		Assert.Equal(0x0001 | 0x0004 | 0x0010 | 0x0020, ace.AccessMask);
		Assert.Contains(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid));
		Assert.Contains(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
	}

	[Fact]
	public void GrantUserControl_Twice_SingleAce()
	{
		var once = ServiceSetup.GrantUserControl(Binary(new RawSecurityDescriptor(DefaultServiceSddl)), User);

		var twice = new RawSecurityDescriptor(ServiceSetup.GrantUserControl(once, User), 0);

		Assert.Single(twice.DiscretionaryAcl!.Cast<CommonAce>(), a => a.SecurityIdentifier == User);
	}

	[Fact]
	public void PrepareDataDirectory_CreatesLogs_InheritableFullControlForAccount()
	{
		var dataDir = new DataDirectory(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
		var account = WindowsIdentity.GetCurrent().User!;
		try
		{
			ServiceSetup.PrepareDataDirectory(dataDir, account);

			Assert.True(Directory.Exists(dataDir.Logs));
			var rules = new DirectoryInfo(dataDir.Root).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
			Assert.Contains(rules, r => r.IdentityReference == account
				&& r.AccessControlType == AccessControlType.Allow
				&& r.FileSystemRights.HasFlag(FileSystemRights.FullControl)
				&& r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
		}
		finally
		{
			TryDelete(dataDir.Root);
		}
	}

	[Theory]
	[InlineData(@"--admin|install|HOME\jane", "install", @"HOME\jane")]
	[InlineData(@"--admin|uninstall|HOME\jane", "uninstall", @"HOME\jane")]
	public void AdminCommand_Parse_Valid(string args, string command, string user)
	{
		Assert.Equal((command, user), AdminCommand.Parse(Args(args)));
	}

	[Theory]
	[InlineData("")]
	[InlineData(@"--admin")]
	[InlineData(@"--admin|install")]
	[InlineData(@"--admin|format|HOME\jane")]
	[InlineData(@"--admin|install| ")]
	[InlineData(@"--admin|install|HOME\jane|extra")]
	[InlineData(@"--veloapp-install|1.0.0")]
	public void AdminCommand_Parse_Invalid(string args)
	{
		Assert.Null(AdminCommand.Parse(Args(args)));
	}

	[Fact]
	public void AdminCommand_Arguments_QuoteTheUser_NoPassword()
	{
		Assert.Equal(@"--admin install ""HOME\jane doe""", AdminCommand.Arguments(AdminCommand.Install, @"HOME\jane doe"));
	}

	[Fact]
	public void AdminCommand_RunUninstall_ExitCodes()
	{
		var service = new FakeServiceControl();

		Assert.Equal(0, AdminCommand.RunUninstall(service));
		Assert.Equal(["uninstall"], service.Calls);
		Assert.Equal(1, AdminCommand.RunUninstall(new FakeServiceControl { FailUninstall = new InvalidOperationException("access denied") }));
	}

	[Fact]
	public void InstallViewModel_DefaultsToTrayUser_RequiresPassword()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var vm = new InstallViewModel(service, @"HOME\jane");

		vm.InstallCommand.Execute(null);

		Assert.Equal(@"HOME\jane", vm.Account);
		Assert.Equal("Enter the account and its Windows password.", vm.Error);
		Assert.Empty(service.Calls);
		Assert.False(vm.Succeeded);
	}

	[Fact]
	public async Task InstallViewModel_Success_InstallsAsAccount_ControlForTrayUser_ClearsPassword()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var vm = new InstallViewModel(service, @"HOME\jane") { Account = @" HOME\svc ", Password = "p@ss word" };

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal([@"install HOME\svc p@ss word HOME\jane"], service.Calls);
		Assert.True(vm.Succeeded);
		Assert.Equal(string.Empty, vm.Password);
		Assert.Null(vm.Error);
		Assert.False(vm.IsBusy);
	}

	[Fact]
	public async Task InstallViewModel_Failure_ShowsMessage_KeepsPasswordForRetry()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled) { FailInstall = new InvalidOperationException("Wrong password") };
		var vm = new InstallViewModel(service, @"HOME\jane") { Password = "typo" };

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal("Wrong password", vm.Error);
		Assert.False(vm.Succeeded);
		Assert.Equal("typo", vm.Password);
		Assert.True(vm.InstallCommand.CanExecute(null));
	}

	private static void TryDelete(string path)
	{
		try
		{
			Directory.Delete(path, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort.
		}
	}

	/// <summary>Command line as '|'-separated arguments (attributes cannot hold string arrays as data).</summary>
	private static string[] Args(string joined) => joined.Length == 0 ? [] : joined.Split('|');

	private static byte[] Binary(RawSecurityDescriptor descriptor)
	{
		var bytes = new byte[descriptor.BinaryLength];
		descriptor.GetBinaryForm(bytes, 0);
		return bytes;
	}
}
