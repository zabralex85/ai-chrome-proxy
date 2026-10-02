using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary>The decisions behind service installation, kept out of the P/Invoke code so they are unit-tested.</summary>
public static class ServiceSetup
{
	public const string DisplayName = "AI Chrome Proxy";

	/// <summary>SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP: tray Start/Stop/Restart without UAC.</summary>
	public const int UserControlRights = 0x0001 | 0x0004 | 0x0010 | 0x0020;

	/// <summary>Failure actions: restart after <see cref="RestartDelay"/>, this many times; the count resets after <see cref="FailureResetPeriod"/>.</summary>
	public const int RestartAttempts = 3;

	public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan FailureResetPeriod = TimeSpan.FromDays(1);

	/// <summary>The Server published self-contained into <c>server\</c> next to the tray (Velopack's stable <c>current\</c> folder).</summary>
	public static string ServerExecutable(string trayDirectory) => Path.Combine(trayDirectory, "server", "AiChromeProxy.Server.exe");

	/// <summary>Always quoted: the path is under the user's profile and may contain spaces (unquoted service paths are also a privilege-escalation hole).</summary>
	public static string BinaryPathName(string executable) => $"\"{executable}\"";

	/// <summary>Splits <c>DOMAIN\user</c>; a bare user name is a local account (<c>.</c>).</summary>
	public static (string Domain, string User) SplitAccount(string account)
	{
		var trimmed = account.Trim();
		var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
		return separator < 0 ? (".", trimmed) : (trimmed[..separator], trimmed[(separator + 1)..]);
	}

	/// <summary>Account name for CreateService: <c>DOMAIN\user</c>, or <c>.\user</c> for a local account.</summary>
	public static string ServiceStartName(string account)
	{
		var (domain, user) = SplitAccount(account);
		return $"{domain}\\{user}";
	}

	public static SecurityIdentifier Sid(string account)
	{
		var (domain, user) = SplitAccount(account);
		var name = domain == "." ? new NTAccount(Environment.MachineName, user) : new NTAccount(domain, user);
		return (SecurityIdentifier)name.Translate(typeof(SecurityIdentifier));
	}

	/// <summary>Adds an allow ACE with <see cref="UserControlRights"/> for <paramref name="user"/> to a service's self-relative security descriptor.</summary>
	public static byte[] GrantUserControl(byte[] securityDescriptor, SecurityIdentifier user)
	{
		var descriptor = new CommonSecurityDescriptor(isContainer: false, isDS: false, new RawSecurityDescriptor(securityDescriptor, 0));
		var dacl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("The service has no DACL to extend.");
		dacl.AddAccess(AccessControlType.Allow, user, UserControlRights, InheritanceFlags.None, PropagationFlags.None);
		var bytes = new byte[descriptor.BinaryLength];
		descriptor.GetBinaryForm(bytes, 0);
		return bytes;
	}

	/// <summary>Creates <c>&lt;DataDir&gt;</c> and <c>logs</c> with inheritable full control for the service account.</summary>
	public static void PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account) => PrepareDataDirectory(dataDir, account, account);

	/// <summary>As above, refusing a root or <c>logs</c> that is a link or was created by an untrusted user (<c>%ProgramData%</c> lets standard users pre-create folders).</summary>
	public static void PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser)
	{
		// Both handles stay open until the ACL is written, so neither folder can be swapped for a link after it was checked.
		using var rootGuard = DataDirectoryGuard.Acquire(dataDir.Root);
		using var logsGuard = DataDirectoryGuard.Acquire(dataDir.Logs);
		EnsureDataDirectorySafe(dataDir, account, controlUser);
		var root = new DirectoryInfo(dataDir.Root);
		var security = root.GetAccessControl();
		security.AddAccessRule(new FileSystemAccessRule(
			account,
			FileSystemRights.FullControl,
			InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
			PropagationFlags.None,
			AccessControlType.Allow));
		root.SetAccessControl(security);
	}

	/// <summary>Throws when <c>&lt;DataDir&gt;</c> or its <c>logs</c> already exists as a link or with an owner other than Administrators, SYSTEM, the service account or the control user.</summary>
	public static void EnsureDataDirectorySafe(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser)
	{
		foreach (var path in new[] { dataDir.Root, dataDir.Logs })
		{
			var info = new DirectoryInfo(path);
			if (!info.Exists)
			{
				continue;
			}

			if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
			{
				throw new InvalidOperationException($"{path} is a link; delete it and retry.");
			}

			var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
			if (!IsTrustedOwner(owner, account, controlUser))
			{
				throw new InvalidOperationException($"{path} was created by another user; delete it and retry.");
			}
		}
	}

	public static bool IsTrustedOwner(SecurityIdentifier? owner, params SecurityIdentifier[] trusted) =>
		owner is not null
		&& (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || trusted.Contains(owner));

	/// <summary>The service binary lives in the tray user's writable profile: running it as anyone else would hand that account to the user.</summary>
	public static void EnsureServiceAccountIsControlUser(SecurityIdentifier account, SecurityIdentifier controlUser)
	{
		if (account != controlUser)
		{
			throw new InvalidOperationException(
				"The service binary is in your user profile, so the service must run as that same user. Enter your own account.");
		}
	}

	/// <summary>Delete first (survives the caller being killed), then a best-effort stop, then wait for it to stop.</summary>
	public static void RunUninstallSequence(Action delete, Action stop, Action waitStopped)
	{
		delete();
		try
		{
			stop();
		}
		catch (InvalidOperationException)
		{
			// Already stopped or stopping (1061/1062): the wait below decides.
		}

		waitStopped();
	}
}
