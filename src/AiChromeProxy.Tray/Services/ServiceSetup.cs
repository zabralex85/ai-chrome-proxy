using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary>The decisions behind service installation, kept out of the P/Invoke code so they are unit-tested.</summary>
public static class ServiceSetup
{
	public const string DisplayName = "AI Chrome Proxy";

	/// <summary><c>NT SERVICE\TrustedInstaller</c>.</summary>
	public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

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

	/// <summary>Creates <c>&lt;DataDir&gt;</c> and <c>logs</c> with the <see cref="DataDirectoryDacl"/> (existing children are not rewritten).</summary>
	public static void PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account) => PrepareDataDirectory(dataDir, account, account);

	/// <summary>
	/// As above, refusing a root or <c>logs</c> that is a link, and a root or <c>logs</c> (or an entry in them) created by an untrusted user:
	/// <c>%ProgramData%</c> lets standard users pre-create folders and files.
	/// </summary>
	public static void PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser) =>
		PrepareDataDirectory(dataDir, account, controlUser, DataDirectoryGuard.OwnerOf);

	/// <summary>As above, reading owners through <paramref name="ownerOf"/> (the real one is <see cref="DataDirectoryGuard.OwnerOf"/>; tests simulate a foreign owner without admin rights).</summary>
	public static void PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser, Func<string, SecurityIdentifier?> ownerOf)
	{
		var dacl = DataDirectoryDacl(account);

		// Root first: nothing is created inside it before it was checked. The handle stays open until the DACL is written.
		using var rootGuard = DataDirectoryGuard.Acquire(dataDir.Root);
		EnsureDirectorySafe(dataDir.Root, account, controlUser, ownerOf);
		WriteDacl(rootGuard, dacl);

		// Checked after the DACL: from now on only SYSTEM, Administrators and the account can add entries, so the check cannot be raced.
		EnsureEntriesTrusted(dataDir.Root, account, controlUser, ownerOf);

		// A new logs inherits the set; an existing one (it may carry the inherited %ProgramData% ACEs) gets it written. Its files are left as they are.
		using var logsGuard = DataDirectoryGuard.Acquire(dataDir.Logs);
		EnsureDirectorySafe(dataDir.Logs, account, controlUser, ownerOf);
		WriteDacl(logsGuard, dacl);
		EnsureEntriesTrusted(dataDir.Logs, account, controlUser, ownerOf);

		// The held handles share WRITE, so either could in theory be turned into a junction in place meanwhile: re-check.
		foreach (var path in new[] { dataDir.Root, dataDir.Logs })
		{
			if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				throw new InvalidOperationException($"{path} is a link; delete it and retry.");
			}
		}
	}

	/// <summary>
	/// Before the tray (not elevated) writes the settings file, which holds the tunnel token, possibly before any install: a missing
	/// <c>&lt;DataDir&gt;</c> is created with the <see cref="DataDirectoryDacl"/> of <paramref name="user"/>, one the user owns gets it as
	/// install writes it, and one owned by Administrators or SYSTEM (an install made it) must already be protected. Links and untrusted owners are refused.
	/// </summary>
	public static void PrepareSettingsDirectory(DataDirectory dataDir, SecurityIdentifier user, Func<string, SecurityIdentifier?> ownerOf)
	{
		if (!Directory.Exists(dataDir.Root))
		{
			// Created protected: inheriting the %ProgramData% ACEs, even briefly, would let other users read or add files.
			var security = new DirectorySecurity();
			security.SetSecurityDescriptorSddlForm(DataDirectoryDacl(user), AccessControlSections.Access);
			Directory.CreateDirectory(Path.GetDirectoryName(dataDir.Root)!);
			security.CreateDirectory(dataDir.Root);
		}

		if (ownerOf(dataDir.Root) == user)
		{
			PrepareDataDirectory(dataDir, user, user, ownerOf);
			return;
		}

		// Not ours: the user cannot change its DACL.
		EnsureDataDirectorySafe(dataDir, user, user, ownerOf);
		if (!new DirectoryInfo(dataDir.Root).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected)
		{
			throw new InvalidOperationException($"{dataDir.Root} is not protected; run Install service, or delete the folder, and retry.");
		}
	}

	/// <summary>The settings file's own protected DACL, set when it is created: SYSTEM and Administrators Full Control, <paramref name="user"/> Modify.</summary>
	public static string SettingsFileDacl(SecurityIdentifier user) =>
		new RawSecurityDescriptor($"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;{user.Value})").GetSddlForm(AccessControlSections.Access);

	/// <summary>
	/// The protected DACL (no ACEs inherited from <c>%ProgramData%</c>) of the root and <c>logs</c>, as normalized SDDL: SYSTEM and Administrators
	/// Full Control, the service account Modify, all inherited by new children; nothing for Users, Authenticated Users or Everyone.
	/// </summary>
	public static string DataDirectoryDacl(SecurityIdentifier account) =>
		new RawSecurityDescriptor($"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;{account.Value})").GetSddlForm(AccessControlSections.Access);

	/// <summary>Throws when <c>&lt;DataDir&gt;</c> or its <c>logs</c> already exists as a link, or it or an entry in it has an untrusted owner (see <see cref="IsTrustedOwner"/>).</summary>
	public static void EnsureDataDirectorySafe(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser) =>
		EnsureDataDirectorySafe(dataDir, account, controlUser, DataDirectoryGuard.OwnerOf);

	/// <summary>As above, reading owners through <paramref name="ownerOf"/>.</summary>
	public static void EnsureDataDirectorySafe(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser, Func<string, SecurityIdentifier?> ownerOf)
	{
		foreach (var path in new[] { dataDir.Root, dataDir.Logs })
		{
			EnsureDirectorySafe(path, account, controlUser, ownerOf);
			if (Directory.Exists(path))
			{
				EnsureEntriesTrusted(path, account, controlUser, ownerOf);
			}
		}
	}

	/// <summary>Throws for the first path whose owner (read by <paramref name="ownerOf"/>) is not trusted: anyone else could still rewrite it.</summary>
	public static void EnsureOwnersTrusted(IEnumerable<string> paths, Func<string, SecurityIdentifier?> ownerOf, params SecurityIdentifier[] trusted)
	{
		foreach (var path in paths)
		{
			if (!IsTrustedOwner(ownerOf(path), trusted))
			{
				throw new InvalidOperationException($"{path} was created by another user; delete it and retry.");
			}
		}
	}

	/// <summary>Administrators, SYSTEM, TrustedInstaller or one of <paramref name="trusted"/> (the service account).</summary>
	public static bool IsTrustedOwner(SecurityIdentifier? owner, params SecurityIdentifier[] trusted) =>
		owner is not null
		&& (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.Value == TrustedInstallerSid || trusted.Contains(owner));

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

	private static void EnsureDirectorySafe(string path, SecurityIdentifier account, SecurityIdentifier controlUser, Func<string, SecurityIdentifier?> ownerOf)
	{
		var info = new DirectoryInfo(path);
		if (!info.Exists)
		{
			return;
		}

		if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
		{
			throw new InvalidOperationException($"{path} is a link; delete it and retry.");
		}

		EnsureOwnersTrusted([path], ownerOf, account, controlUser);
	}

	private static void EnsureEntriesTrusted(string directory, SecurityIdentifier account, SecurityIdentifier controlUser, Func<string, SecurityIdentifier?> ownerOf) =>
		EnsureOwnersTrusted(Directory.EnumerateFileSystemEntries(directory), ownerOf, account, controlUser);

	/// <summary>Writes the DACL only when the stored one differs in any way (protected flag, ACE, rights, inheritance or propagation flags).</summary>
	private static void WriteDacl(DataDirectoryGuard guard, string dacl)
	{
		if (guard.GetDaclSddl() != dacl)
		{
			guard.SetDacl(dacl);
		}
	}
}
