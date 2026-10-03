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

	/// <summary>Tries of a rename or delete of a server folder in the data directory (<see cref="RetryDelay"/> apart).</summary>
	public const int RetryAttempts = 10;

	/// <summary>SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP: tray Start/Stop/Restart without UAC.</summary>
	public const int UserControlRights = 0x0001 | 0x0004 | 0x0010 | 0x0020;

	/// <summary>Failure actions: restart after <see cref="RestartDelay"/>, this many times; the count resets after <see cref="FailureResetPeriod"/>.</summary>
	public const int RestartAttempts = 3;

	public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan FailureResetPeriod = TimeSpan.FromDays(1);

	/// <summary>The Server's executable, in <c>server\</c> of the package and of the data directory.</summary>
	public static readonly string ServerFileName = "AiChromeProxy.Server.exe";

	/// <summary>Pause between attempts to rename or delete a server folder: the SCM reports Stopped a moment before the process has let go of its files.</summary>
	private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

	/// <summary>
	/// The service binary: the copy in the default data directory, outside the app folder (Setup.exe cannot replace a folder the running
	/// service holds). <c>AICP_DATA_DIR</c> is ignored, as in the elevated install.
	/// </summary>
	public static string ServiceExecutable => ServerExecutable(DataDirectory.Resolve(null).Root);

	/// <summary>The Server in <c>server\</c> under <paramref name="directory"/>: the tray's folder (the package) or the data directory (the service's copy).</summary>
	public static string ServerExecutable(string directory) => Path.Combine(directory, "server", ServerFileName);

	/// <summary>
	/// Whether the SCM's binary path (quoted or not) is exactly <paramref name="executable"/>; false for a service installed by an older version,
	/// which still runs from the app folder until "Install service…" is run again.
	/// </summary>
	public static bool RunsFrom(string? binaryPathName, string executable)
	{
		var path = binaryPathName?.Trim() ?? string.Empty;
		if (path.StartsWith('"'))
		{
			var end = path.IndexOf('"', 1);
			path = end < 0 ? string.Empty : path[1..end];
		}

		return path.Length > 0 && string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Replaces <c>&lt;DataDir&gt;\server</c> with a copy of the package's <paramref name="source"/> folder; the service must be stopped (its files are in use).
	/// Runs only as the tray user (never elevated): the folder is writable by that user. The copy goes to <c>server.new</c>, then <c>server</c> becomes
	/// <c>server.old</c>, <c>server.new</c> becomes <c>server</c> and <c>server.old</c> is deleted: any failure up to the swap leaves the old
	/// <c>server</c> in place. Leftovers of an interrupted sync are cleaned up first (<see cref="CleanUpServerLeftovers"/>).
	/// </summary>
	/// <param name="attempts">Tries of the rename of <c>server</c> and of each delete (<see cref="RetryDelay"/> apart).</param>
	public static void SyncServerDirectory(string source, DataDirectory dataDir, int attempts = RetryAttempts)
	{
		var target = dataDir.Server;
		var staging = target + ".new";
		var old = target + ".old";

		CleanUpServerLeftovers(dataDir, attempts);
		if (!File.Exists(Path.Combine(source, ServerFileName)))
		{
			throw new InvalidOperationException($"{source} has no {ServerFileName}; reinstall the app.");
		}

		Directory.CreateDirectory(staging);
		CopyTree(source, staging);

		if (Directory.Exists(target))
		{
			Retry(() => Directory.Move(target, old), attempts);
		}

		// Fresh files: an antivirus or indexer may hold them for a moment, like the stopped service holds the old ones.
		try
		{
			Retry(() => Directory.Move(staging, target), attempts);
		}
		catch
		{
			if (Directory.Exists(old) && !Directory.Exists(target))
			{
				Retry(() => Directory.Move(old, target), attempts);
			}

			throw;
		}

		try
		{
			DeleteDirectory(old, attempts);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The new copy is in place; the next sync, tray start or uninstall removes the old one.
		}
	}

	/// <summary>
	/// Repairs what an interrupted sync left (as the tray user: at tray start and before every sync): <c>server</c> missing but <c>server.old</c>
	/// present means the swap stopped between its two renames, so <c>server.old</c> (the last working copy) goes back; <c>server.new</c> and
	/// <c>server.old</c> are deleted.
	/// </summary>
	public static void CleanUpServerLeftovers(DataDirectory dataDir, int attempts = RetryAttempts)
	{
		var old = dataDir.Server + ".old";
		if (!Directory.Exists(dataDir.Server) && Directory.Exists(old))
		{
			Directory.Move(old, dataDir.Server);
		}

		DeleteDirectory(dataDir.Server + ".new", attempts);
		DeleteDirectory(old, attempts);
	}

	/// <summary>Uninstall: deletes the service's copy of the Server and leftovers of an interrupted sync; settings and logs are kept.</summary>
	public static void DeleteServerDirectory(DataDirectory dataDir, int attempts = RetryAttempts)
	{
		foreach (var path in new[] { dataDir.Server, dataDir.Server + ".new", dataDir.Server + ".old" })
		{
			try
			{
				DeleteDirectory(path, attempts);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				throw new IOException($"The service was removed, but {path} could not be deleted ({ex.Message}); delete it by hand.", ex);
			}
		}
	}

	/// <summary>Elevated install: refuses unless the tray already copied the Server to <c>&lt;DataDir&gt;\server</c> (a real folder and file, not links).</summary>
	public static void EnsureServerCopied(DataDirectory dataDir)
	{
		var executable = ServerExecutable(dataDir.Root);
		if (!File.Exists(executable))
		{
			throw new InvalidOperationException($"{executable} is missing; run Install service… from the tray menu (it copies the Server there first).");
		}

		foreach (var path in new[] { dataDir.Server, executable })
		{
			if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
			{
				throw new InvalidOperationException($"{path} is a link; delete it and retry.");
			}
		}
	}

	/// <summary>
	/// The tray side of <c>--admin install|uninstall</c>: every file operation runs here, as the user, so the elevated instance never copies,
	/// renames or deletes anything in a folder the user can write (it only does SCM, LSA and DACL work).
	/// Install: stop the service if it runs (the user has SERVICE_STOP; its files are in use), <paramref name="syncServer"/> off the UI thread,
	/// run the elevated install, then start the service: always after a successful install (re-running it after a password change must bring an
	/// unattended server back), else only when it was running. A failed sync is not elevated. While a running service is stopped for this,
	/// <paramref name="pendingMarker"/> exists, so a tray killed meanwhile has it started again at the next tray start.
	/// Uninstall: the elevated instance deletes the service; on success <paramref name="deleteServer"/> removes its copy of the Server.
	/// </summary>
	/// <returns>The elevated instance's exit code (null: UAC declined).</returns>
	public static async Task<int?> RunAdminCommandAsync(string command, IServiceControl service, Action syncServer, Action deleteServer, Func<string, Task<int?>> runElevated, string pendingMarker)
	{
		if (command == AdminCommand.Uninstall)
		{
			var uninstalled = await runElevated(command);
			if (uninstalled == 0)
			{
				deleteServer();
			}

			return uninstalled;
		}

		var wasRunning = service.GetState() is ServiceState.Running or ServiceState.Starting;
		if (wasRunning)
		{
			await File.WriteAllTextAsync(pendingMarker, string.Empty);
		}

		try
		{
			int? exitCode;
			try
			{
				if (wasRunning)
				{
					await service.StopAsync(CancellationToken.None);
				}

				await Task.Run(syncServer);
				exitCode = await runElevated(command);
			}
			catch
			{
				if (wasRunning)
				{
					try
					{
						await service.StartAsync(CancellationToken.None);
					}
					catch (Exception)
					{
						// The first error is the one to show; the tray status shows the service stopped.
					}
				}

				throw;
			}

			if ((exitCode == 0 || wasRunning) && service.GetState() == ServiceState.Stopped)
			{
				await service.StartAsync(CancellationToken.None);
			}

			return exitCode;
		}
		finally
		{
			if (wasRunning)
			{
				File.Delete(pendingMarker);
			}
		}
	}

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
		using (var rootGuard = DataDirectoryGuard.Acquire(dataDir.Root))
		{
			EnsureDirectorySafe(dataDir.Root, account, controlUser, ownerOf);
			WriteDacl(rootGuard, dacl);

			// Checked after the DACL: from now on only SYSTEM, Administrators and the account can add entries, so the check cannot be raced.
			EnsureEntriesTrusted(dataDir.Root, account, controlUser, ownerOf);

			// A new logs inherits the set; an existing one (it may carry the inherited %ProgramData% ACEs) gets it written. Its files are left as they are.
			using (var logsGuard = DataDirectoryGuard.Acquire(dataDir.Logs))
			{
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
		}
	}

	/// <summary>
	/// Before the tray (not elevated) writes the settings file, which holds the tunnel token, possibly before any install: a missing
	/// <c>&lt;DataDir&gt;</c> is created with the <see cref="DataDirectoryDacl"/> of <paramref name="user"/>, one the user owns gets it as
	/// install writes it, and one owned by Administrators or SYSTEM (an install made it) must already be protected. Links and untrusted owners are refused.
	/// </summary>
	/// <param name="tokenOwner">The owner this process gives what it creates (<see cref="System.Security.Principal.WindowsIdentity.Owner"/>): the user, or Administrators when elevated; a folder it owns is treated as the user's own.</param>
	public static void PrepareSettingsDirectory(DataDirectory dataDir, SecurityIdentifier user, Func<string, SecurityIdentifier?> ownerOf, SecurityIdentifier? tokenOwner = null)
	{
		if (!Directory.Exists(dataDir.Root))
		{
			// Created protected: inheriting the %ProgramData% ACEs, even briefly, would let other users read or add files.
			var security = new DirectorySecurity();
			security.SetSecurityDescriptorSddlForm(DataDirectoryDacl(user), AccessControlSections.Access);
			Directory.CreateDirectory(Path.GetDirectoryName(dataDir.Root)!);
			security.CreateDirectory(dataDir.Root);
		}

		var owner = ownerOf(dataDir.Root);
		if (owner == user || (owner is not null && owner == tokenOwner))
		{
			PrepareDataDirectory(dataDir, user, user, ownerOf);
			return;
		}

		// Not ours: the user cannot change its DACL.
		EnsureDataDirectorySafe(dataDir, user, user, ownerOf);
		if (!new DirectoryInfo(dataDir.Root).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected)
		{
			throw new InvalidOperationException(AskAdministratorToDelete(dataDir.Root, "is not protected"));
		}
	}

	/// <summary>
	/// The way out when the tray (not elevated) cannot use a data folder it does not own: "Install service…" prepares it as the user too, so only
	/// an administrator can remove it.
	/// </summary>
	public static string AskAdministratorToDelete(string root, string problem) =>
		$"{root} {problem}. Ask an administrator to delete it (back up appsettings.json and the logs folder first: they are deleted with it), then retry.";

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

	/// <summary>The tray user replaces the service binaries on every update (without UAC): running them as anyone else would hand that account to the user.</summary>
	public static void EnsureServiceAccountIsControlUser(SecurityIdentifier account, SecurityIdentifier controlUser)
	{
		if (account != controlUser)
		{
			throw new InvalidOperationException(
				"Updates replace the service's files as you, so the service must run as that same user. Enter your own account.");
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

	/// <summary>Files and folders; links are skipped, never followed out of the package.</summary>
	private static void CopyTree(string source, string target)
	{
		foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
		{
			if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				continue;
			}

			var destination = Path.Combine(target, entry.Name);
			if (entry is DirectoryInfo directory)
			{
				Directory.CreateDirectory(destination);
				CopyTree(directory.FullName, destination);
			}
			else
			{
				((FileInfo)entry).CopyTo(destination);
			}
		}
	}

	/// <summary>Removes the folder, or only the link when it is one (<see cref="Directory.Delete(string, bool)"/> does not follow links).</summary>
	private static void DeleteDirectory(string path, int attempts)
	{
		if (Directory.Exists(path))
		{
			Retry(() => Directory.Delete(path, recursive: true), attempts);
		}
	}

	private static void Retry(Action action, int attempts)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				action();
				return;
			}
			catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
			{
				Thread.Sleep(RetryDelay);
			}
		}
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
