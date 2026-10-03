# Remote Access Wizard (2b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After `Setup.exe`, the tray's **Set up remote access…** wizard takes one Cloudflare API token, a zone, a subdomain and the allowed emails, creates or reuses the Cloudflare Tunnel, DNS record, Access policy and Access application, and writes the Server settings; the Server runs the bundled `cloudflared` itself, so the tunnel is up after power-on without anyone logging in.

**Architecture:** A thin Cloudflare API v4 client, input validation and an idempotent `RemoteAccessProvisioner` live in Infrastructure (`AiChromeProxy.Infrastructure.Cloudflare`, no IO besides the injected `HttpClient`). The Server gets `CloudflaredSupervisor` (a `BackgroundService`) that starts `cloudflared tunnel --no-autoupdate run` with the token in the child's environment, restarts it with capped exponential backoff and kills it on shutdown; the real process and the Win32 Job object sit in a thin `[ExcludeFromCodeCoverage]` class behind `ICloudflaredProcess`. The tray gets `RemoteAccessViewModel` + `RemoteAccessWindow` (three stages) and a shared `SettingsFile` helper (atomic merge-write) used by both the wizard and the Settings window. The release workflow bundles a pinned, hash-verified `cloudflared.exe`.

**Tech Stack:** .NET 10; System.Text.Json (snake_case naming policy) and `HttpClient`; Microsoft.Extensions.Hosting `BackgroundService`; Avalonia 12.1.3 + CommunityToolkit.Mvvm 8.4.2 (existing); xunit v3 + Microsoft.Extensions.TimeProvider.Testing 10.10.0 (already referenced by `tests/AiChromeProxy.Tests`); Avalonia.Headless 12.1.3 (existing). No new packages.

**Spec:** [docs/superpowers/specs/2026-10-03-remote-access-wizard-design.md](../specs/2026-10-03-remote-access-wizard-design.md).

**Verification:** every code block below was built and run on a scratch prototype of this branch (not committed): `dotnet build -c Release` → 0 warnings, 0 errors after every task; the suite grows 243 → 245 → 251 → 265 → 294 → 304 → 315 → 331 → 331 tests, all passing, total line coverage 97.8% at the end; the full suite was re-run 6× and the supervisor tests 15× with no flake.

## Global Constraints

- **Safety while implementing:** do not install/remove real Windows services, grant LSA rights, change DACLs outside temp dirs the test creates, write HKCU/HKLM, trigger UAC, open GUI windows on the desktop, run cloudflared, call the real Cloudflare API, or touch the real %ProgramData%. No personal domains (use example.com). No Claude/AI attribution in commits. UTF-8 without BOM. Never name a source folder Logs/Log/Release/Debug/bin/obj.
- Tests use `FakeCloudflareHandler` (no network) and the fake `ICloudflaredProcess` (no process). Do not set a `Tunnel__Token` environment variable in the shell that runs the tests (test hosts would then try to start `cloudflared`). Use `AICP_DATA_DIR` (never the real `%ProgramData%`) for any local run of the Server or the tray.
- Dependency direction unchanged: Domain ← Application ← Infrastructure ← Server; Client → Domain only; **Tray → Domain and Infrastructure only**; the Cloudflare client is in Infrastructure, the supervisor in the Server (architecture tests in `tests/AiChromeProxy.Tests/Architecture` must stay green unchanged).
- Style: tabs in C#; no `this.`; private fields `_camelCase`; sorted usings (`System*` first); file-scoped namespaces; one top-level type per file; XML doc comments in the style of the existing code; StyleCop errors fail the build — fix code, never the ruleset. English only.
- Exact values from the spec (copy, don't retype):
  - API base `https://api.cloudflare.com/client/v4/`, bearer user API token, envelope `{ success, errors[{code,message}], result, result_info }`, `per_page` 50, 429 → wait `Retry-After` capped at 60 s, once.
  - Tunnel name `ai-chrome-proxy-<machine name, lower-case>`, `config_src: "cloudflare"`; ingress `<fqdn>` → `http://127.0.0.1:<Server:Port>` then `http_status:404`.
  - DNS: `CNAME <fqdn>` → `<tunnelId>.cfargotunnel.com`, `proxied: true`, `ttl: 1`; foreign record → `"<host> already has a DNS record; choose another subdomain or delete it."`.
  - Access policy name `AI Chrome Proxy — <fqdn>` (em dash), `decision: "allow"`, `include: [{email:{email}}…]`; application name `AI Chrome Proxy`, `type: "self_hosted"`, `session_duration: "24h"`, `policies: [{id, precedence: 1}]`, `aud` read back.
  - Access not enabled → `"Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry."`.
  - Token permissions: Account — *Cloudflare Tunnel: Edit*, *Access: Apps and Policies: Edit*, *Access: Organizations, Identity Providers, and Groups: Read*; Zone — *DNS: Edit*, *Zone: Read*. "Create token" opens `https://dash.cloudflare.com/profile/api-tokens`.
  - Settings written: `CloudflareAccess:TeamDomain`, `CloudflareAccess:Audience`, `Server:PublicHost`, `Tunnel:Token` (secret; never shown, never logged).
  - `cloudflared tunnel --no-autoupdate run`, token only in the child's `TUNNEL_TOKEN`; binary `Tunnel:CloudflaredPath` → `<AppContext.BaseDirectory>\cloudflared.exe` → `cloudflared` on PATH; backoff 1 s, 2 s, 4 s … capped at 60 s, reset after a run ≥ 5 min; Job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.
  - Release: cloudflared `2026.9.3`, SHA256 `f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2` (checked against the real asset while writing this plan), URL `https://github.com/cloudflare/cloudflared/releases/download/2026.9.3/cloudflared-windows-amd64.exe`, copied to `publish/server/cloudflared.exe`; version and hash are job-level `env`.
- Commands (from the repo root):
  - Build: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
  - Gate: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → `failed: 0`, exit code 0.
  - One class: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<Namespace.ClassName>"`.
- Before each commit, `git status --short` must list every new file (a `.gitignore`d folder name would drop files silently). Commit only the files of the task.

## Decisions taken while planning (spec ambiguities)

The user delegated these; each is the smallest reading of the spec that satisfies it.

1. **API client shape.** `CloudflareApi` is the envelope/transport (`SendAsync<T>`, paginated `ListAsync<T>`, bearer, error mapping, 429) plus the two calls the wizard makes directly (`VerifyTokenAsync`, `ListZonesAsync`). The tunnel/DNS/Access endpoints and their DTOs live, private, in `RemoteAccessProvisioner` — same requests as the spec, fewer public types.
2. **429.** `Retry-After` as seconds is honoured up to 60 s; a missing header (or the HTTP-date form) waits the 60 s cap. The wait is an injectable `Func<TimeSpan, CancellationToken, Task>` (tests record it) rather than a `TimeProvider`.
3. **"Access not enabled".** Any 4xx except 429 on `GET accounts/{a}/access/organizations`, or a result without `auth_domain`. The message starts with the spec's sentence and keeps Cloudflare's own error in parentheses (a token missing *Access: Organizations … Read* also lands here, and the user sees why). 5xx is reported as is.
4. **DNS conflict.** Any record for the name that is not a CNAME to this tunnel (content compared case-insensitively) refuses — even when our CNAME is also there.
5. **Lookups.** Policies: list all reusable policies, exact name match. Applications: list all, domain match ignoring case. Tunnel: `?name=&is_deleted=false`, exact name match.
6. **Progress.** One line per finished step (6 lines), then the wizard adds `Settings saved to <file>`.
7. **Supervisor seam.** A delegate `Func<ProcessStartInfo, Action<string>, ICloudflaredProcess>` (no factory interface). The supervisor takes the bound `TunnelOptions` once at start; a changed token needs a service restart, which the wizard offers.
8. **Job object.** One kill-on-close job per Server process (created lazily, never closed by code: Windows closes it when the Server dies). P/Invoke via `DllImport` + `#pragma warning disable SYSLIB1054` (as `tests/…/RawDacl.cs` does), so the internet-facing Server does not need `AllowUnsafeBlocks`.
9. **Service offers after success.** Not installed → **Install service…** (existing elevated flow); Running/Starting → **Restart service**; Stopped → neither, status says it applies at the next start.
10. **First-run auto-open.** `NeedsSetup` = no `Server:PublicHost` in the settings file; a broken JSON file counts as "needs setup"; an unreadable file (IO/ACL error) does not auto-open (the wizard could not write it either).
11. **Settings window.** Keeps `Tunnel:Token` untouched (unknown keys survive the merge) and never shows it; its environment-variable warning now also lists `Tunnel__*`.
12. **API token lifetime.** Kept in the view model until setup succeeds (so **Back** + retry works after a failure), then cleared together with the API client.
13. **%TEMP% leak.** It does not reproduce on the current branch (the 243 tests leave nothing; the leftover folders carry the old `@mt` CLEF format or come from killed runs whose `Dispose` never ran). Fix: a regression assertion that the CLEF file is closed once the logging container is disposed, plus an xunit v3 assembly fixture that deletes `%TEMP%\aicp-tests` after every run (which also sweeps what a killed run left).
14. **Owner seam.** New overload `ServiceSetup.PrepareDataDirectory(dataDir, account, controlUser, Func<string, SecurityIdentifier?> ownerOf)`; the existing 3-argument overload passes `DataDirectoryGuard.OwnerOf`. The directory itself is now checked through the same lookup (a link is opened, not followed) instead of `GetAccessControl`.
15. **Subdomain input** is trimmed and lower-cased before validation; emails are split on commas and newlines, trimmed, de-duplicated ignoring case.
16. **`RemoteAccessResult.ToString()`** is overridden so a logged result can never print the tunnel token.
17. **"Create token…" / "Open"** are window code-behind (`App.Open`, now `internal`); the view model has no process seam.
18. **Release** also copies `THIRD-PARTY-NOTICES.md` into the package root (Apache-2.0 redistribution notice).
19. `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs` contains a stray BEL character (`@"D:<BEL>icp"`, from an unescaped `\a`); Task 2 rewrites that test and fixes it to `@"D:\aicp"`.

## File map

| File | Task | Responsibility |
|---|---|---|
| `src/AiChromeProxy.Tray/Services/ServiceSetup.cs` (modify) | 1 | Injectable owner lookup for `PrepareDataDirectory` |
| `tests/AiChromeProxy.Tests/TempRootCleanup.cs` (new) | 1 | Assembly fixture: delete `%TEMP%\aicp-tests` after the run |
| `src/AiChromeProxy.Tray/Services/SettingsFile.cs` (new) | 2 | Load / merge / atomic write of `<DataDir>\appsettings.json` |
| `src/AiChromeProxy.Tray/ViewModels/SettingsViewModel.cs` (modify) | 2 | Uses `SettingsFile`; warns about `Tunnel__*` |
| `src/AiChromeProxy.Infrastructure/Hosting/TunnelOptions.cs` (new) | 3 | Section `Tunnel`: `Token`, `CloudflaredPath` |
| `src/AiChromeProxy.Infrastructure/Cloudflare/CloudflareApi.cs`, `CloudflareApiException.cs`, `CloudflareZone.cs`, `CloudflareAccount.cs` (new) | 3 | API v4 transport + token verify + zones |
| `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessInput.cs` (new) | 4 | Subdomain / email / FQDN validation |
| `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessProvisioner.cs`, `RemoteAccessRequest.cs`, `RemoteAccessResult.cs` (new) | 5 | The six idempotent provisioning steps |
| `src/AiChromeProxy.Server/Hosting/ICloudflaredProcess.cs`, `CloudflaredSupervisor.cs`, `CloudflaredProcess.cs` (new); `src/AiChromeProxy.Server/Program.cs` (modify) | 6 | Run and supervise `cloudflared` |
| `src/AiChromeProxy.Tray/ViewModels/RemoteAccessViewModel.cs` (new) | 7 | Wizard stages, validation, provisioning, settings, service offers |
| `src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml(.cs)` (new); `src/AiChromeProxy.Tray/App.axaml.cs` (modify) | 8 | Wizard window, tray menu item, first-run auto-open |
| `.github/workflows/release.yml` (modify); `THIRD-PARTY-NOTICES.md` (new) | 9 | Bundle pinned, verified `cloudflared.exe` |
| `docs/windows-host.md`, `docs/setup/cloudflare.md`, `README.md`, `CLAUDE.md` (modify) | 10 | Remote access docs, checklist |

---

### Task 1: 2a deferred minors — injectable owner lookup; %TEMP% test leak

**Files:**
- Modify: `src/AiChromeProxy.Tray/Services/ServiceSetup.cs`
- Modify: `tests/AiChromeProxy.Tests/Tray/ServiceSetupSecurityTests.cs`
- Modify: `tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs`
- Create: `tests/AiChromeProxy.Tests/TempRootCleanup.cs`

**Interfaces:**
- Consumes: `DataDirectoryGuard.OwnerOf(string) : SecurityIdentifier?`, `ServiceSetup.EnsureOwnersTrusted(IEnumerable<string>, Func<string, SecurityIdentifier?>, params SecurityIdentifier[])` (existing).
- Produces: `public static void ServiceSetup.PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser, Func<string, SecurityIdentifier?> ownerOf)`; the existing overloads keep their signatures. `public sealed class AiChromeProxy.Tests.TempRootCleanup : IDisposable` with `public static readonly string Root` (`%TEMP%\aicp-tests`).

- [ ] **Step 1: Write the failing tests**

In `tests/AiChromeProxy.Tests/Tray/ServiceSetupSecurityTests.cs`, insert these two tests just before the `[Fact]` attribute of `PrepareDataDirectory_ControlUserOwnedFolder_Allowed`:

```csharp
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

```

In `tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs`, at the end of `Logging_WithDataDirectory_WritesDailyClefFile` (after the `Assert.Equal("clef", json.RootElement.GetProperty("Name").GetString());` line), add:

```csharp

		// Disposing the container closed the file: nothing holds it open, so the folder can be deleted.
		File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
```

Create `tests/AiChromeProxy.Tests/TempRootCleanup.cs`:

```csharp
[assembly: AssemblyFixture(typeof(AiChromeProxy.Tests.TempRootCleanup))]

namespace AiChromeProxy.Tests;

/// <summary>
/// Deletes <c>%TEMP%\aicp-tests</c> once the whole run is over: each test class removes its own folder, but a run that was killed
/// or crashed never ran those Dispose methods, so their folders would pile up. The next run sweeps them.
/// </summary>
public sealed class TempRootCleanup : IDisposable
{
	public static readonly string Root = Path.Combine(Path.GetTempPath(), "aicp-tests");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(Root))
			{
				Directory.Delete(Root, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort: another test run may still be using its folder.
		}
	}
}
```

- [ ] **Step 2: Run the build to verify the new tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS1501: No overload for method 'PrepareDataDirectory' takes 4 arguments` (twice, in `ServiceSetupSecurityTests.cs`).

(The closed-file assertion already holds: `AddServerLogging` registers Serilog with `dispose: true` since 2a. It is a regression guard for the leak the spec names, not a red test.)

- [ ] **Step 3: Implement the owner seam**

Replace the whole content of `src/AiChromeProxy.Tray/Services/ServiceSetup.cs` with:

```csharp
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
	/// The protected DACL (no ACEs inherited from <c>%ProgramData%</c>) of the root and <c>logs</c>, as normalized SDDL: SYSTEM and Administrators
	/// Full Control, the service account Modify, all inherited by new children; nothing for Users, Authenticated Users or Everyone.
	/// </summary>
	public static string DataDirectoryDacl(SecurityIdentifier account) =>
		new RawSecurityDescriptor($"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;{account.Value})").GetSddlForm(AccessControlSections.Access);

	/// <summary>Throws when <c>&lt;DataDir&gt;</c> or its <c>logs</c> already exists as a link, or it or an entry in it has an untrusted owner (see <see cref="IsTrustedOwner"/>).</summary>
	public static void EnsureDataDirectorySafe(DataDirectory dataDir, SecurityIdentifier account, SecurityIdentifier controlUser)
	{
		foreach (var path in new[] { dataDir.Root, dataDir.Logs })
		{
			EnsureDirectorySafe(path, account, controlUser, DataDirectoryGuard.OwnerOf);
			if (Directory.Exists(path))
			{
				EnsureEntriesTrusted(path, account, controlUser, DataDirectoryGuard.OwnerOf);
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
```

What changed versus the current file (for review): the 3-argument `PrepareDataDirectory` now forwards to the new 4-argument overload with `DataDirectoryGuard.OwnerOf`; `EnsureDirectorySafe` and `EnsureEntriesTrusted` take the `ownerOf` lookup (the directory's own owner is read through it via `EnsureOwnersTrusted([path], …)` instead of `GetAccessControl`); `EnsureDataDirectorySafe` passes `DataDirectoryGuard.OwnerOf`. Nothing else.

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 245`, `failed: 0`, exit code 0.

Then check the sweep: `Test-Path "$env:TEMP\aicp-tests"` (PowerShell) → `False`.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Tray/Services/ServiceSetup.cs tests/AiChromeProxy.Tests/Tray/ServiceSetupSecurityTests.cs tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs tests/AiChromeProxy.Tests/TempRootCleanup.cs
git status --short
git commit -m "fix(tray): injectable owner lookup for the data directory; sweep %TEMP%\aicp-tests after test runs"
```

---

### Task 2: Shared `SettingsFile` helper (extracted from the Settings window)

**Files:**
- Create: `src/AiChromeProxy.Tray/Services/SettingsFile.cs`
- Modify: `src/AiChromeProxy.Tray/ViewModels/SettingsViewModel.cs` (whole file)
- Create: `tests/AiChromeProxy.Tests/Tray/SettingsFileTests.cs`
- Delete: `tests/AiChromeProxy.Tests/Tray/SettingsAtomicWriteTests.cs` (its three tests move to `SettingsFileTests`, rewritten against the helper)
- Modify: `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs` (two tests)

**Interfaces:**
- Consumes: `DataDirectory.SettingsFile`, `DataDirectory.Root` (existing).
- Produces (namespace `AiChromeProxy.Tray.Services`, `public static class SettingsFile`):
  - `JsonObject Load(DataDirectory dataDir)` — empty when missing; throws `JsonException` when broken.
  - `JsonObject LoadOrEmpty(DataDirectory dataDir)` — broken counts as empty.
  - `JsonObject Section(JsonObject settings, string name)` — get or create an object section.
  - `void Update(DataDirectory dataDir, Action<JsonObject> change)` — load (broken = empty), change, write `.tmp` then replace; throws `IOException("Could not write <file>: …")`.
- Removed: `SettingsViewModel.Load(DataDirectory)` (only used inside the class; no other caller in `src/` or `tests/` — check with `rg -n "SettingsViewModel.Load" src tests` → no output).

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Tray/SettingsFileTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

public sealed class SettingsFileTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	private string TmpPath => _dataDir.SettingsFile + ".tmp";

	[Fact]
	public void Load_NoFile_Empty()
	{
		Assert.Empty(SettingsFile.Load(_dataDir));
	}

	[Fact]
	public void Load_BrokenFile_Throws_LoadOrEmpty_Empty()
	{
		WriteFile("{ not json");

		Assert.ThrowsAny<JsonException>(() => SettingsFile.Load(_dataDir));
		Assert.Empty(SettingsFile.LoadOrEmpty(_dataDir));
	}

	[Fact]
	public void Section_ExistingKept_MissingOrNotAnObjectCreated()
	{
		var settings = JsonNode.Parse("""{ "Server": { "Port": 1 }, "Tunnel": "x" }""")!.AsObject();

		Assert.Equal(1, (int?)SettingsFile.Section(settings, "Server")["Port"]);
		SettingsFile.Section(settings, "Tunnel")["Token"] = "t";
		SettingsFile.Section(settings, "CloudflareAccess")["Audience"] = "a";

		Assert.True(JsonNode.DeepEquals(
			JsonNode.Parse("""{ "Server": { "Port": 1 }, "Tunnel": { "Token": "t" }, "CloudflareAccess": { "Audience": "a" } }"""),
			settings));
	}

	[Fact]
	public void Update_MergesIntoExistingFile_KeepsUnknownKeys()
	{
		WriteFile("""{ "Serilog": { "MinimumLevel": "Debug" }, "Server": { "Port": 6000, "Extra": true } }""");

		SettingsFile.Update(_dataDir, s => SettingsFile.Section(s, "Server")["PublicHost"] = "code.example.com");

		Assert.True(JsonNode.DeepEquals(
			JsonNode.Parse("""{ "Serilog": { "MinimumLevel": "Debug" }, "Server": { "Port": 6000, "Extra": true, "PublicHost": "code.example.com" } }"""),
			ReadFile()));
	}

	[Fact]
	public void Update_NoFolderYet_CreatesItAndAnIndentedFile()
	{
		SettingsFile.Update(_dataDir, s => SettingsFile.Section(s, "Tunnel")["Token"] = "t");

		Assert.Equal("t", (string?)ReadFile()["Tunnel"]?["Token"]);
		Assert.Contains(Environment.NewLine + "  ", File.ReadAllText(_dataDir.SettingsFile), StringComparison.Ordinal);
	}

	[Fact]
	public void Update_BrokenFile_Replaced()
	{
		WriteFile("{ not json");

		SettingsFile.Update(_dataDir, s => SettingsFile.Section(s, "Server")["Port"] = 5180);

		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "Server": { "Port": 5180 } }"""), ReadFile()));
	}

	[Fact]
	public void Update_StaleTemporaryFileFromPreviousCrash_RemovedAfterSuccessfulWrite()
	{
		WriteFile("{}");
		File.WriteAllText(TmpPath, "{ stale tmp from crash }");

		SettingsFile.Update(_dataDir, s => s["A"] = 1);

		Assert.False(File.Exists(TmpPath));
		Assert.Equal(1, (int?)ReadFile()["A"]);
	}

	[Fact]
	public void Update_SuccessiveWrites_NoTemporaryFileLeftBehind()
	{
		SettingsFile.Update(_dataDir, s => s["A"] = 1);
		Assert.False(File.Exists(TmpPath));

		SettingsFile.Update(_dataDir, s => s["A"] = 2);

		Assert.False(File.Exists(TmpPath));
		Assert.Equal(2, (int?)ReadFile()["A"]);
	}

	[Fact]
	public void Update_FileNotWritable_IOExceptionNamesTheFile_NoTemporaryFileLeft()
	{
		Directory.CreateDirectory(_dataDir.SettingsFile);

		var ex = Assert.Throws<IOException>(() => SettingsFile.Update(_dataDir, s => s["A"] = 1));

		Assert.StartsWith($"Could not write {_dataDir.SettingsFile}: ", ex.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(TmpPath));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;
}
```

Delete the old file: `git rm tests/AiChromeProxy.Tests/Tray/SettingsAtomicWriteTests.cs`

In `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs`, replace the whole method `OverridingVariables_AccessServerAndEnvironmentNames_CaseInsensitive_Sorted` with (adds `TUNNEL__TOKEN`; also fixes the stray BEL character in the `AICP_DATA_DIR` value):

```csharp
	[Fact]
	public void OverridingVariables_AccessServerAndEnvironmentNames_CaseInsensitive_Sorted()
	{
		var environment = new Dictionary<string, string>
		{
			["Server__PublicHost"] = "code.example.com",
			["PATH"] = @"C:\Windows",
			["cloudflareaccess__Audience"] = "aud",
			["ASPNETCORE_ENVIRONMENT"] = "Development",
			["DOTNET_ENVIRONMENT"] = "Development",
			["ASPNETCORE_URLS"] = "http://+:80",
			["ServerName"] = "x",
			["Serilog__MinimumLevel"] = "Debug",
			["AICP_DATA_DIR"] = @"D:\aicp",
			["TUNNEL__TOKEN"] = "secret",
		};

		Assert.Equal(
			["AICP_DATA_DIR", "ASPNETCORE_ENVIRONMENT", "cloudflareaccess__Audience", "DOTNET_ENVIRONMENT", "Serilog__MinimumLevel", "Server__PublicHost", "TUNNEL__TOKEN"],
			SettingsViewModel.OverridingVariables(environment));
		Assert.Empty(SettingsViewModel.OverridingVariables(new Dictionary<string, string> { ["PATH"] = "x" }));
	}
```

and replace the whole method `Save_Valid_WritesExpectedJson_KeepsOtherKeys_SetsAutoStart` with (the wizard's `Tunnel:Token` must survive a Settings save):

```csharp
	[Fact]
	public void Save_Valid_WritesExpectedJson_KeepsOtherKeys_SetsAutoStart()
	{
		WriteFile("""{ "Serilog": { "MinimumLevel": { "Default": "Debug" } }, "Server": { "Port": 5180, "Extra": true }, "Tunnel": { "Token": "secret" } }""");
		var vm = Valid(Create());
		vm.Port = " 6001 ";
		vm.StartWithWindows = true;

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		var expected = JsonNode.Parse("""
			{
			  "Serilog": { "MinimumLevel": { "Default": "Debug" } },
			  "Server": { "Port": 6001, "Extra": true, "PublicHost": "code.example.com" },
			  "Tunnel": { "Token": "secret" },
			  "CloudflareAccess": { "TeamDomain": "team.cloudflareaccess.com", "Audience": "aud" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadFile()), ReadFile().ToJsonString());
		Assert.True(_autoStart.IsEnabled);
	}
```

Check the BEL is gone: `rg -n "\x07" tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs` → no output.

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0103: The name 'SettingsFile' does not exist in the current context` (in `SettingsFileTests.cs`).

- [ ] **Step 3: Implement the helper and use it in the Settings window**

Create `src/AiChromeProxy.Tray/Services/SettingsFile.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary><c>&lt;DataDir&gt;\appsettings.json</c> as a JSON object: the Settings window and the remote access wizard read and write it the same way, keeping keys they do not know.</summary>
public static class SettingsFile
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	/// <summary>The settings file as a JSON object (empty when it does not exist yet).</summary>
	/// <exception cref="JsonException">The file is not a JSON object.</exception>
	public static JsonObject Load(DataDirectory dataDir) =>
		File.Exists(dataDir.SettingsFile) ? JsonNode.Parse(File.ReadAllText(dataDir.SettingsFile))?.AsObject() ?? [] : [];

	/// <summary>As <see cref="Load"/>, but a broken file counts as empty (saving replaces it).</summary>
	public static JsonObject LoadOrEmpty(DataDirectory dataDir)
	{
		try
		{
			return Load(dataDir);
		}
		catch (JsonException)
		{
			return [];
		}
	}

	/// <summary>The object under <paramref name="name"/>, created (or replacing a non-object value) when missing.</summary>
	public static JsonObject Section(JsonObject settings, string name)
	{
		if (settings[name] is JsonObject section)
		{
			return section;
		}

		section = [];
		settings[name] = section;
		return section;
	}

	/// <summary>Reads the file (a broken one counts as empty), lets <paramref name="change"/> set values, then writes it atomically: a temporary file, then a replace.</summary>
	/// <exception cref="IOException">The file could not be written; the message names it. The old file is left as it was.</exception>
	public static void Update(DataDirectory dataDir, Action<JsonObject> change)
	{
		var settings = LoadOrEmpty(dataDir);
		change(settings);

		Directory.CreateDirectory(dataDir.Root);
		var tmpPath = dataDir.SettingsFile + ".tmp";
		try
		{
			File.WriteAllText(tmpPath, settings.ToJsonString(Indented));
			File.Move(tmpPath, dataDir.SettingsFile, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			try
			{
				File.Delete(tmpPath);
			}
			catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
			{
				// Best effort; the original failure is what the user needs to see.
			}

			throw new IOException($"Could not write {dataDir.SettingsFile}: {ex.Message}", ex);
		}
	}
}
```

Replace the whole content of `src/AiChromeProxy.Tray/ViewModels/SettingsViewModel.cs` with:

```csharp
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Edits <c>&lt;DataDir&gt;\appsettings.json</c> with the Server's own validation rules; other keys in the file (e.g. <c>Tunnel:Token</c>) are kept.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
	private readonly DataDirectory _dataDir;
	private readonly IAutoStart _autoStart;
	private readonly IServiceControl _service;

	public SettingsViewModel(DataDirectory dataDir, IAutoStart autoStart, IServiceControl service)
	{
		_dataDir = dataDir;
		_autoStart = autoStart;
		_service = service;

		var settings = new JsonObject();
		try
		{
			settings = SettingsFile.Load(dataDir);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			Errors = [$"Could not read {dataDir.SettingsFile}: {ex.Message} Saving replaces the file."];
		}

		TeamDomain = (string?)settings[CloudflareAccessOptions.Section]?[nameof(CloudflareAccessOptions.TeamDomain)] ?? string.Empty;
		Audience = (string?)settings[CloudflareAccessOptions.Section]?[nameof(CloudflareAccessOptions.Audience)] ?? string.Empty;
		PublicHost = (string?)settings[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)] ?? string.Empty;
		Port = settings[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString() ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
		StartWithWindows = autoStart.IsEnabled;

		var overriding = OverridingVariables(Environment.GetEnvironmentVariables());
		EnvironmentWarning = overriding.Count == 0
			? null
			: $"Environment variables override these settings if the service sees them: {string.Join(", ", overriding)}. Remove them.";
	}

	[ObservableProperty]
	public partial string TeamDomain { get; set; }

	[ObservableProperty]
	public partial string Audience { get; set; }

	[ObservableProperty]
	public partial string PublicHost { get; set; }

	[ObservableProperty]
	public partial string Port { get; set; }

	[ObservableProperty]
	public partial bool StartWithWindows { get; set; }

	/// <summary>One line naming the variables from <see cref="OverridingVariables"/>; null when there are none.</summary>
	public string? EnvironmentWarning { get; }

	[ObservableProperty]
	public partial IReadOnlyList<string> Errors { get; private set; } = [];

	[ObservableProperty]
	public partial string? Status { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
	public partial bool IsRestartOffered { get; private set; }

	/// <summary>
	/// Variables of <paramref name="environment"/> that win over <c>appsettings.json</c> (env vars come later in the Server's configuration),
	/// typically left over from running the Server from source.
	/// </summary>
	public static IReadOnlyList<string> OverridingVariables(IDictionary environment) =>
		environment.Keys.Cast<string>()
			.Where(name => name.StartsWith("CloudflareAccess__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Server__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Serilog__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Tunnel__", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("AICP_DATA_DIR", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <summary>"Open UI": through the tunnel when a public host is set (local requests carry no Access token), else loopback.</summary>
	public static Uri UiAddress(DataDirectory dataDir)
	{
		var settings = SettingsFile.LoadOrEmpty(dataDir);
		var publicHost = (string?)settings[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)];
		var port = settings[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString() ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
		return string.IsNullOrWhiteSpace(publicHost) ? new Uri($"http://127.0.0.1:{port}/") : new Uri($"https://{publicHost}/");
	}

	/// <returns>The messages the Server would fail with for these values (empty when valid).</returns>
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		if (ParsePort() is null)
		{
			errors.Add("Server:Port must be a number from 1 to 65535.");
		}

		if (new ServerOptions { PublicHost = PublicHost.Trim() }.GetError(isDevelopment: false) is { } serverError)
		{
			errors.Add(serverError);
		}

		if (new CloudflareAccessOptions { TeamDomain = TeamDomain.Trim(), Audience = Audience.Trim() }.GetError(isDevelopment: false) is { } accessError)
		{
			errors.Add(accessError);
		}

		return errors;
	}

	[RelayCommand]
	private void Save()
	{
		Status = null;
		IsRestartOffered = false;
		Errors = Validate();
		if (Errors.Count > 0)
		{
			return;
		}

		try
		{
			SettingsFile.Update(_dataDir, settings =>
			{
				var access = SettingsFile.Section(settings, CloudflareAccessOptions.Section);
				access[nameof(CloudflareAccessOptions.TeamDomain)] = TeamDomain.Trim();
				access[nameof(CloudflareAccessOptions.Audience)] = Audience.Trim();
				var server = SettingsFile.Section(settings, ServerOptions.Section);
				server[nameof(ServerOptions.Port)] = ParsePort();
				server[nameof(ServerOptions.PublicHost)] = PublicHost.Trim();
			});
			_autoStart.IsEnabled = StartWithWindows;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Errors = [ex.Message];
			return;
		}

		IsRestartOffered = _service.GetState() is ServiceState.Running or ServiceState.Starting;
		Status = IsRestartOffered ? "Saved. Restart the service to apply." : "Saved. Applied when the service starts.";
	}

	[RelayCommand(CanExecute = nameof(IsRestartOffered))]
	private async Task RestartServiceAsync()
	{
		try
		{
			await _service.StopAsync(CancellationToken.None);
			await _service.StartAsync(CancellationToken.None);
			IsRestartOffered = false;
			Status = "Service restarted.";
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
	}

	private int? ParsePort() =>
		int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run the gate → `total: 251`, `failed: 0`, exit code 0. (`OverridingVariables…` was red before Step 3: it needs the `Tunnel__` prefix.)

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Tray/Services/SettingsFile.cs src/AiChromeProxy.Tray/ViewModels/SettingsViewModel.cs tests/AiChromeProxy.Tests/Tray/SettingsFileTests.cs tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs
git status --short
git commit -m "refactor(tray): extract SettingsFile (merge + atomic write) from the Settings window"
```

(`git rm` in Step 1 already staged the deletion of `SettingsAtomicWriteTests.cs`.)

---

### Task 3: `TunnelOptions` and the Cloudflare API client

**Files:**
- Create: `src/AiChromeProxy.Infrastructure/Hosting/TunnelOptions.cs`
- Create: `src/AiChromeProxy.Infrastructure/Cloudflare/CloudflareApiException.cs`, `CloudflareAccount.cs`, `CloudflareZone.cs`, `CloudflareApi.cs`
- Create: `tests/AiChromeProxy.Tests/Infrastructure/FakeCloudflareHandler.cs`, `tests/AiChromeProxy.Tests/Infrastructure/CloudflareApiTests.cs`

**Interfaces:**
- Produces (namespace `AiChromeProxy.Infrastructure.Hosting`): `public sealed class TunnelOptions { const string Section = "Tunnel"; string Token; string CloudflaredPath; }` (both default `string.Empty`).
- Produces (namespace `AiChromeProxy.Infrastructure.Cloudflare`):
  - `public sealed class CloudflareApiException(string message, int code = 0, HttpStatusCode? statusCode = null) : Exception` with `int Code`, `HttpStatusCode? StatusCode`.
  - `public sealed record CloudflareAccount(string Id, string Name)`; `public sealed record CloudflareZone(string Id, string Name, CloudflareAccount Account)`.
  - `public sealed class CloudflareApi(HttpClient http, string apiToken, Func<TimeSpan, CancellationToken, Task>? delay = null)` with `const string BaseUrl = "https://api.cloudflare.com/client/v4/"`, `const int PageSize = 50`, `static readonly TimeSpan MaxRetryAfter` (60 s), `Task VerifyTokenAsync(CancellationToken)`, `Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(CancellationToken)`, `Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)`, `Task<IReadOnlyList<T>> ListAsync<T>(string path, CancellationToken ct)`. Paths are relative to `BaseUrl`; bodies are serialized with snake_case names (`ConfigSrc` → `config_src`).
- Produces (tests, namespace `AiChromeProxy.Tests.Infrastructure`): `FakeCloudflareHandler` — `On(method, path, resultJson, totalPages?)`, `OnError(method, path, status, code, message)`, `OnResponse(method, path, params Func<HttpResponseMessage>[])` (registering a route again replaces it), `Requests` (`Request(Method, Path, Authorization, Body)`), `Calls`, `Body(method, path)`, static `Envelope`, `ErrorEnvelope`, `Json`. Used again by Tasks 5, 7 and 8.

- [ ] **Step 1: Write the fake handler and the failing tests**

Create `tests/AiChromeProxy.Tests/Infrastructure/FakeCloudflareHandler.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

/// <summary>
/// In-memory Cloudflare API: canned v4 envelopes per <c>"METHOD path?query"</c> (relative to <see cref="CloudflareApi.BaseUrl"/>),
/// every request recorded. Registering a route again replaces it; a route given several responses plays them in order and then repeats
/// the last; an unknown route answers 404.
/// </summary>
public sealed class FakeCloudflareHandler : HttpMessageHandler
{
	private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes = [];

	public List<Request> Requests { get; } = [];

	/// <summary>"METHOD path?query" of each request, in order.</summary>
	public IEnumerable<string> Calls => Requests.Select(r => $"{r.Method} {r.Path}");

	/// <summary><c>{"success":true,"errors":[],"result":<paramref name="resultJson"/>}</c>, plus <c>result_info</c> when <paramref name="totalPages"/> is given.</summary>
	public static string Envelope(string resultJson, int? totalPages = null)
	{
		var resultInfo = totalPages is null ? string.Empty : $$""","result_info":{"page":1,"per_page":50,"total_pages":{{totalPages}}}""";
		return $$"""{"success":true,"errors":[],"messages":[],"result":{{resultJson}}{{resultInfo}}}""";
	}

	public static string ErrorEnvelope(int code, string message) =>
		$$"""{"success":false,"errors":[{"code":{{code}},"message":"{{message}}"}],"messages":[],"result":null}""";

	public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
		new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	/// <summary>A successful call returning <paramref name="resultJson"/>.</summary>
	public FakeCloudflareHandler On(string method, string path, string resultJson, int? totalPages = null) =>
		OnResponse(method, path, () => Json(HttpStatusCode.OK, Envelope(resultJson, totalPages)));

	/// <summary>A failed call: <paramref name="status"/> with one API error.</summary>
	public FakeCloudflareHandler OnError(string method, string path, HttpStatusCode status, int code, string message) =>
		OnResponse(method, path, () => Json(status, ErrorEnvelope(code, message)));

	public FakeCloudflareHandler OnResponse(string method, string path, params Func<HttpResponseMessage>[] responses)
	{
		_routes[$"{method} {path}"] = new Queue<Func<HttpResponseMessage>>(responses);
		return this;
	}

	/// <summary>The JSON body of the only request to "METHOD path".</summary>
	public JsonNode? Body(string method, string path) => Assert.Single(Requests, r => r.Method == method && r.Path == path).Body;

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var url = request.RequestUri!.AbsoluteUri;
		Assert.StartsWith(CloudflareApi.BaseUrl, url, StringComparison.Ordinal);
		var path = url[CloudflareApi.BaseUrl.Length..];
		var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
		Requests.Add(new Request(request.Method.Method, path, request.Headers.Authorization?.ToString(), body));

		if (!_routes.TryGetValue($"{request.Method.Method} {path}", out var queue))
		{
			return Json(HttpStatusCode.NotFound, ErrorEnvelope(7003, $"No route for {request.Method.Method} {path}"));
		}

		return (queue.Count > 1 ? queue.Dequeue() : queue.Peek())();
	}

	public sealed record Request(string Method, string Path, string? Authorization, JsonNode? Body);
}
```

Create `tests/AiChromeProxy.Tests/Infrastructure/CloudflareApiTests.cs`:

```csharp
using System.Net;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class CloudflareApiTests : IDisposable
{
	private const string Token = "secret-api-token-123";

	private readonly FakeCloudflareHandler _handler = new();
	private readonly HttpClient _http;
	private readonly List<TimeSpan> _waits = [];
	private readonly CloudflareApi _api;

	public CloudflareApiTests()
	{
		_http = new HttpClient(_handler);
		_api = new CloudflareApi(_http, Token, (wait, _) =>
		{
			_waits.Add(wait);
			return Task.CompletedTask;
		});
	}

	[Fact]
	public async Task VerifyToken_Active_SendsBearerToken()
	{
		_handler.On("GET", "user/tokens/verify", """{"id":"t1","status":"active"}""");

		await _api.VerifyTokenAsync(TestContext.Current.CancellationToken);

		var request = Assert.Single(_handler.Requests);
		Assert.Equal($"Bearer {Token}", request.Authorization);
		Assert.Null(request.Body);
	}

	[Fact]
	public async Task VerifyToken_NotActive_Throws()
	{
		_handler.On("GET", "user/tokens/verify", """{"id":"t1","status":"disabled"}""");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("The API token is disabled, not active.", ex.Message);
	}

	[Fact]
	public async Task ListZones_ActiveOnly_WithAccounts()
	{
		_handler.On("GET", "zones?status=active&page=1&per_page=50", """[{"id":"z1","name":"example.com","status":"active","account":{"id":"a1","name":"Jane's account"}}]""", totalPages: 1);

		var zones = await _api.ListZonesAsync(TestContext.Current.CancellationToken);

		Assert.Equal([new CloudflareZone("z1", "example.com", new CloudflareAccount("a1", "Jane's account"))], zones);
	}

	[Fact]
	public async Task List_FollowsTotalPages()
	{
		_handler
			.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"p1"}]""", totalPages: 3)
			.On("GET", "accounts/a1/access/apps?page=2&per_page=50", """[{"id":"p2"}]""", totalPages: 3)
			.On("GET", "accounts/a1/access/apps?page=3&per_page=50", """[{"id":"p3"}]""", totalPages: 3);

		var items = await _api.ListAsync<Item>("accounts/a1/access/apps", TestContext.Current.CancellationToken);

		Assert.Equal(["p1", "p2", "p3"], items.Select(i => i.Id));
		Assert.Equal(3, _handler.Requests.Count);
	}

	[Fact]
	public async Task List_NoResultInfo_SinglePage()
	{
		_handler.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"p1"}]""");

		var items = await _api.ListAsync<Item>("accounts/a1/access/apps", TestContext.Current.CancellationToken);

		Assert.Equal("p1", Assert.Single(items).Id);
	}

	[Fact]
	public async Task Send_BodyAsSnakeCaseJson()
	{
		_handler.On("POST", "accounts/a1/cfd_tunnel", """{"id":"t1","name":"n"}""");

		var item = await _api.SendAsync<Item>(HttpMethod.Post, "accounts/a1/cfd_tunnel", new { Name = "n", ConfigSrc = "cloudflare" }, TestContext.Current.CancellationToken);

		Assert.Equal("t1", item.Id);
		Assert.Equal("""{"name":"n","config_src":"cloudflare"}""", _handler.Body("POST", "accounts/a1/cfd_tunnel")!.ToJsonString());
	}

	[Fact]
	public async Task SuccessFalse_ThrowsFirstError_WithoutToken()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => FakeCloudflareHandler.Json(HttpStatusCode.OK, FakeCloudflareHandler.ErrorEnvelope(1000, "Invalid API Token")));

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API error 1000 on GET user/tokens/verify: Invalid API Token", ex.Message);
		Assert.Equal(1000, ex.Code);
		Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
		Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Non2xx_WithEnvelope_ThrowsFirstError_PathWithoutQuery()
	{
		_handler.OnError("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", HttpStatusCode.Forbidden, 10000, "Authentication error");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.ListAsync<Item>("zones/z1/dns_records?name=code.example.com", TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API error 10000 on GET zones/z1/dns_records: Authentication error", ex.Message);
		Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
		Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Non2xx_NotJson_ThrowsStatus()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>") });

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API returned HTTP 502 on GET user/tokens/verify.", ex.Message);
		Assert.Equal(0, ex.Code);
	}

	[Fact]
	public async Task Success_NotJson_ThrowsUnreadable()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ok</html>") });

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.StartsWith("Cloudflare API returned an unreadable response on GET user/tokens/verify", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Success_NullResult_Throws()
	{
		_handler.On("GET", "user/tokens/verify", "null");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API returned no result on GET user/tokens/verify.", ex.Message);
	}

	[Fact]
	public async Task TooManyRequests_WaitsRetryAfterOnce_ThenSucceeds()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => RateLimited(TimeSpan.FromSeconds(7)), Active);

		await _api.VerifyTokenAsync(TestContext.Current.CancellationToken);

		Assert.Equal([TimeSpan.FromSeconds(7)], _waits);
		Assert.Equal(2, _handler.Requests.Count);
	}

	[Fact]
	public async Task TooManyRequests_RetryAfterCappedAt60s_SecondOneFails()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => RateLimited(TimeSpan.FromMinutes(10)));

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal([TimeSpan.FromSeconds(60)], _waits);
		Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
		Assert.Equal(2, _handler.Requests.Count);
	}

	[Fact]
	public async Task TooManyRequests_NoRetryAfter_Waits60s()
	{
		_handler.OnResponse(
			"GET",
			"user/tokens/verify",
			() => FakeCloudflareHandler.Json(HttpStatusCode.TooManyRequests, FakeCloudflareHandler.ErrorEnvelope(971, "Please wait")),
			Active);

		await _api.VerifyTokenAsync(TestContext.Current.CancellationToken);

		Assert.Equal([CloudflareApi.MaxRetryAfter], _waits);
	}

	public void Dispose() => _http.Dispose();

	private static HttpResponseMessage Active() => FakeCloudflareHandler.Json(HttpStatusCode.OK, FakeCloudflareHandler.Envelope("""{"status":"active"}"""));

	private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
	{
		var response = FakeCloudflareHandler.Json(HttpStatusCode.TooManyRequests, FakeCloudflareHandler.ErrorEnvelope(971, "Please wait and consider throttling your request speed"));
		response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
		return response;
	}

	private sealed record Item(string Id);
}
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0234: The type or namespace name 'Cloudflare' does not exist in the namespace 'AiChromeProxy.Infrastructure'`.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Infrastructure/Hosting/TunnelOptions.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Section <c>Tunnel</c>: the Cloudflare Tunnel the Server runs <c>cloudflared</c> for. Written by the tray's remote access wizard.</summary>
public sealed class TunnelOptions
{
	public const string Section = "Tunnel";

	/// <summary>Secret: the tunnel's run token. Empty = no tunnel (the Server does not start <c>cloudflared</c>).</summary>
	public string Token { get; set; } = string.Empty;

	/// <summary>Optional full path of <c>cloudflared</c>; empty = the bundled <c>cloudflared.exe</c> next to the Server, else <c>cloudflared</c> on PATH.</summary>
	public string CloudflaredPath { get; set; } = string.Empty;
}
```

Create `src/AiChromeProxy.Infrastructure/Cloudflare/CloudflareApiException.cs`:

```csharp
using System.Net;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>A failed Cloudflare API call, described by the first error of the response (never the API token).</summary>
public sealed class CloudflareApiException(string message, int code = 0, HttpStatusCode? statusCode = null) : Exception(message)
{
	/// <summary>Cloudflare's error code (<c>errors[0].code</c>); 0 when the response carried none.</summary>
	public int Code { get; } = code;

	/// <summary>HTTP status of the response; null when the failure was not an HTTP error status.</summary>
	public HttpStatusCode? StatusCode { get; } = statusCode;
}
```

Create `src/AiChromeProxy.Infrastructure/Cloudflare/CloudflareAccount.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Cloudflare;

public sealed record CloudflareAccount(string Id, string Name);
```

Create `src/AiChromeProxy.Infrastructure/Cloudflare/CloudflareZone.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>An active zone the API token can read, with the account that owns it.</summary>
public sealed record CloudflareZone(string Id, string Name, CloudflareAccount Account);
```

Create `src/AiChromeProxy.Infrastructure/Cloudflare/CloudflareApi.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>
/// Cloudflare REST API v4 with a user API token: the <c>{ success, errors, result, result_info }</c> envelope, error mapping,
/// one wait on HTTP 429 and pagination. The token goes only into the <c>Authorization</c> header of requests to <see cref="BaseUrl"/>.
/// </summary>
/// <param name="delay">Waits before the 429 retry; tests pass a recorder. Default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
public sealed class CloudflareApi(HttpClient http, string apiToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
	public const string BaseUrl = "https://api.cloudflare.com/client/v4/";
	public const int PageSize = 50;

	/// <summary>Longest <c>Retry-After</c> honoured; also the wait when the header is missing.</summary>
	public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

	/// <summary><c>GET user/tokens/verify</c>: the token exists and is active.</summary>
	public async Task VerifyTokenAsync(CancellationToken ct)
	{
		var token = await SendAsync<TokenStatus>(HttpMethod.Get, "user/tokens/verify", null, ct);
		if (token.Status != "active")
		{
			throw new CloudflareApiException($"The API token is {token.Status}, not active.");
		}
	}

	/// <summary>Active zones the token can read; each carries its account (no account permission needed).</summary>
	public Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(CancellationToken ct) => ListAsync<CloudflareZone>("zones?status=active", ct);

	/// <summary>One call; <paramref name="body"/> (if any) is sent as JSON with snake_case names.</summary>
	/// <returns>The envelope's <c>result</c>.</returns>
	/// <exception cref="CloudflareApiException">Non-2xx status, <c>success: false</c>, a second 429, no result or an unreadable response.</exception>
	public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
	{
		var envelope = await SendEnvelopeAsync<T>(method, path, body, ct);
		return envelope.Result ?? throw new CloudflareApiException($"Cloudflare API returned no result on {method} {PathOnly(path)}.");
	}

	/// <summary>All pages of a list (<c>page</c> and <c>per_page</c> are appended to <paramref name="path"/>), following <c>result_info.total_pages</c>.</summary>
	public async Task<IReadOnlyList<T>> ListAsync<T>(string path, CancellationToken ct)
	{
		var items = new List<T>();
		var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
		for (var page = 1; ; page++)
		{
			var envelope = await SendEnvelopeAsync<List<T>>(HttpMethod.Get, $"{path}{separator}page={page}&per_page={PageSize}", null, ct);
			items.AddRange(envelope.Result ?? []);
			if (page >= (envelope.ResultInfo?.TotalPages ?? 1))
			{
				return items;
			}
		}
	}

	/// <summary>Error messages name the endpoint without its query (an email address may be in it).</summary>
	private static string PathOnly(string path) => path.Split('?')[0];

	private static CloudflareApiException Error(HttpMethod method, string path, HttpStatusCode status, ApiError? first) =>
		first is null
			? new CloudflareApiException($"Cloudflare API returned HTTP {(int)status} on {method} {PathOnly(path)}.", 0, status)
			: new CloudflareApiException($"Cloudflare API error {first.Code} on {method} {PathOnly(path)}: {first.Message}", first.Code, status);

	private async Task<Envelope<T>> SendEnvelopeAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
	{
		var json = body is null ? null : JsonSerializer.Serialize(body, Json);
		for (var attempt = 1; ; attempt++)
		{
			using var request = new HttpRequestMessage(method, BaseUrl + path);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
			if (json is not null)
			{
				request.Content = new StringContent(json, Encoding.UTF8, "application/json");
			}

			using var response = await http.SendAsync(request, ct);
			if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
			{
				var wait = response.Headers.RetryAfter?.Delta ?? MaxRetryAfter;
				await _delay(wait < MaxRetryAfter ? wait : MaxRetryAfter, ct);
				continue;
			}

			Envelope<T>? envelope;
			try
			{
				envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct);
			}
			catch (JsonException) when (!response.IsSuccessStatusCode)
			{
				// Not an API envelope (e.g. an HTML error page from a proxy): report the status.
				envelope = null;
			}
			catch (JsonException ex)
			{
				throw new CloudflareApiException($"Cloudflare API returned an unreadable response on {method} {PathOnly(path)}: {ex.Message}", 0, response.StatusCode);
			}

			if (!response.IsSuccessStatusCode || envelope is not { Success: true })
			{
				throw Error(method, path, response.StatusCode, envelope?.Errors?.FirstOrDefault());
			}

			return envelope;
		}
	}

	private sealed record Envelope<T>(bool Success, IReadOnlyList<ApiError>? Errors, T? Result, ResultInfo? ResultInfo);

	private sealed record ApiError(int Code, string Message);

	private sealed record ResultInfo(int TotalPages);

	private sealed record TokenStatus(string Status);
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Infrastructure.CloudflareApiTests"` → `total: 14`, `failed: 0`.
Run the gate → `total: 265`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Infrastructure/Hosting/TunnelOptions.cs src/AiChromeProxy.Infrastructure/Cloudflare tests/AiChromeProxy.Tests/Infrastructure/FakeCloudflareHandler.cs tests/AiChromeProxy.Tests/Infrastructure/CloudflareApiTests.cs
git status --short
git commit -m "feat(infra): Cloudflare API v4 client (envelope, errors, 429, pagination) and TunnelOptions"
```

---

### Task 4: Wizard input validation

**Files:**
- Create: `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessInput.cs`
- Create: `tests/AiChromeProxy.Tests/Infrastructure/RemoteAccessInputTests.cs`

**Interfaces:**
- Consumes: `HostName.IsValid(string?)` (existing, `AiChromeProxy.Infrastructure.Hosting`).
- Produces (`public static class RemoteAccessInput`, namespace `AiChromeProxy.Infrastructure.Cloudflare`): `bool IsValidSubdomain(string? value)`, `bool IsValidEmail(string value)`, `IReadOnlyList<string> ParseEmails(string? text)`, `IReadOnlyList<string> Validate(string subdomain, string? zoneName, IReadOnlyList<string> emails)` (messages, empty when valid). Note: `Uri.CheckHostName` (behind `HostName`) checks label lengths but not the 253-character total, so the FQDN check fires for malformed zone names (e.g. a trailing dot).

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Infrastructure/RemoteAccessInputTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class RemoteAccessInputTests
{
	[Theory]
	[InlineData("code")]
	[InlineData("a")]
	[InlineData("my-code-2")]
	[InlineData("0")]
	public void Subdomain_OneLabel_Valid(string value)
	{
		Assert.True(RemoteAccessInput.IsValidSubdomain(value));
	}

	[Fact]
	public void Subdomain_63Characters_Valid_64_Invalid()
	{
		Assert.True(RemoteAccessInput.IsValidSubdomain(new string('a', 63)));
		Assert.False(RemoteAccessInput.IsValidSubdomain(new string('a', 64)));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("-code")]
	[InlineData("code-")]
	[InlineData("Code")]
	[InlineData("a.b")]
	[InlineData("co de")]
	[InlineData("code_1")]
	[InlineData("kód")]
	public void Subdomain_NotOneLowerCaseLabel_Invalid(string? value)
	{
		Assert.False(RemoteAccessInput.IsValidSubdomain(value));
	}

	[Theory]
	[InlineData("jane@example.com")]
	[InlineData("jane.doe+code@mail.example.org")]
	public void Email_Valid(string value)
	{
		Assert.True(RemoteAccessInput.IsValidEmail(value));
	}

	[Theory]
	[InlineData("jane")]
	[InlineData("@example.com")]
	[InlineData("jane@example")]
	[InlineData("jane@.com")]
	[InlineData("jane@example.")]
	[InlineData("jane@@example.com")]
	[InlineData("ja ne@example.com")]
	[InlineData("jane@exa@mple.com")]
	public void Email_Invalid(string value)
	{
		Assert.False(RemoteAccessInput.IsValidEmail(value));
	}

	[Fact]
	public void ParseEmails_CommaOrNewline_Trimmed_EmptyAndDuplicatesDropped()
	{
		Assert.Equal(
			["jane@example.com", "joe@example.com", "ann@example.org"],
			RemoteAccessInput.ParseEmails(" jane@example.com, joe@example.com\r\n\nann@example.org ,JANE@example.com,"));
		Assert.Empty(RemoteAccessInput.ParseEmails(null));
		Assert.Empty(RemoteAccessInput.ParseEmails(" , \n"));
	}

	[Fact]
	public void Validate_Valid_NoErrors()
	{
		Assert.Empty(RemoteAccessInput.Validate("code", "example.com", ["jane@example.com"]));
	}

	[Fact]
	public void Validate_Everything_Wrong_OneMessageEach()
	{
		Assert.Equal(
			[
				"Choose a zone.",
				"The subdomain must be one label of a-z, 0-9 and '-', 1 to 63 characters, not starting or ending with '-'.",
				"Enter at least one email address.",
			],
			RemoteAccessInput.Validate("-x", null, []));
	}

	[Fact]
	public void Validate_BadEmail_Named()
	{
		Assert.Equal(["'jane' is not an email address."], RemoteAccessInput.Validate("code", "example.com", ["joe@example.com", "jane"]));
	}

	[Fact]
	public void Validate_ResultingHostNameNotBare_Rejected()
	{
		Assert.Equal(["code.example.com. is not a valid host name."], RemoteAccessInput.Validate("code", "example.com.", ["jane@example.com"]));
	}
}
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0103: The name 'RemoteAccessInput' does not exist in the current context`.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessInput.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>Checks the wizard's details before anything is created in Cloudflare.</summary>
public static class RemoteAccessInput
{
	/// <summary>One DNS label: <c>a-z</c>, <c>0-9</c> and <c>-</c>, 1–63 characters, no leading or trailing <c>-</c>.</summary>
	public static bool IsValidSubdomain(string? value) =>
		value is { Length: >= 1 and <= 63 }
		&& value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
		&& value[0] != '-' && value[^1] != '-';

	/// <summary><c>local@domain</c>: exactly one <c>@</c>, no spaces, a dot inside the domain.</summary>
	public static bool IsValidEmail(string value)
	{
		var at = value.IndexOf('@', StringComparison.Ordinal);
		if (at <= 0 || at != value.LastIndexOf('@') || value.Any(char.IsWhiteSpace))
		{
			return false;
		}

		var domain = value[(at + 1)..];
		var dot = domain.IndexOf('.', StringComparison.Ordinal);
		return dot > 0 && !domain.EndsWith('.');
	}

	/// <summary>Comma- or newline-separated addresses, trimmed, empty entries dropped, duplicates (any case) removed.</summary>
	public static IReadOnlyList<string> ParseEmails(string? text) =>
		(text ?? string.Empty)
			.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <returns>One message per problem; empty when the details can be provisioned.</returns>
	public static IReadOnlyList<string> Validate(string subdomain, string? zoneName, IReadOnlyList<string> emails)
	{
		var errors = new List<string>();
		if (zoneName is null)
		{
			errors.Add("Choose a zone.");
		}

		if (!IsValidSubdomain(subdomain))
		{
			errors.Add("The subdomain must be one label of a-z, 0-9 and '-', 1 to 63 characters, not starting or ending with '-'.");
		}
		else if (zoneName is not null && !HostName.IsValid($"{subdomain}.{zoneName}"))
		{
			errors.Add($"{subdomain}.{zoneName} is not a valid host name.");
		}

		if (emails.Count == 0)
		{
			errors.Add("Enter at least one email address.");
		}

		errors.AddRange(emails.Where(e => !IsValidEmail(e)).Select(e => $"'{e}' is not an email address."));
		return errors;
	}
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run the gate → `total: 294`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessInput.cs tests/AiChromeProxy.Tests/Infrastructure/RemoteAccessInputTests.cs
git status --short
git commit -m "feat(infra): validate the wizard's subdomain, emails and host name"
```

---

### Task 5: `RemoteAccessProvisioner` — tunnel, ingress, DNS, Access policy and application

**Files:**
- Create: `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessRequest.cs`, `RemoteAccessResult.cs`, `RemoteAccessProvisioner.cs`
- Create: `tests/AiChromeProxy.Tests/Infrastructure/RemoteAccessProvisionerTests.cs`

**Interfaces:**
- Consumes: `CloudflareApi.SendAsync<T>`, `CloudflareApi.ListAsync<T>`, `CloudflareApiException`, `CloudflareZone` (Task 3); `FakeCloudflareHandler` (Task 3, tests).
- Produces (namespace `AiChromeProxy.Infrastructure.Cloudflare`):
  - `public sealed record RemoteAccessRequest(CloudflareZone Zone, string Subdomain, IReadOnlyList<string> Emails, int Port, string MachineName)` with `string PublicHost` (`<Subdomain>.<Zone.Name>`).
  - `public sealed record RemoteAccessResult(string TeamDomain, string Audience, string PublicHost, string TunnelToken)` (`ToString()` omits the token).
  - `public sealed class RemoteAccessProvisioner(CloudflareApi api)` with `const string ApplicationName = "AI Chrome Proxy"`, `const string NotEnabledMessage`, `static string TunnelName(string machineName)`, `static string PolicyName(string publicHost)`, `Task<RemoteAccessResult> ProvisionAsync(RemoteAccessRequest request, IProgress<string> progress, CancellationToken ct)`.
- Produces (tests): `public static FakeCloudflareHandler RemoteAccessProvisionerTests.FreshAccount(FakeCloudflareHandler)` and constants `Host`, `TunnelId`, `TunnelToken`, `Policy`, `Zone` — reused by Task 7's view-model tests.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Infrastructure/RemoteAccessProvisionerTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class RemoteAccessProvisionerTests : IDisposable
{
	public const string Host = "code.example.com";
	public const string TunnelId = "c1744f8b-faa1-48a4-9e5c-02ac921467fa";
	public const string TunnelToken = "eyJhIjoidHVubmVsLXRva2VuIn0";
	public const string Policy = "AI Chrome Proxy — code.example.com";

	public static readonly CloudflareZone Zone = new("z1", "example.com", new CloudflareAccount("a1", "Jane's account"));

	private readonly FakeCloudflareHandler _handler = new();
	private readonly HttpClient _http;
	private readonly List<string> _progress = [];

	public RemoteAccessProvisionerTests() => _http = new HttpClient(_handler);

	/// <summary>Cloudflare as a fresh account sees it: Access enabled, nothing of ours exists yet; every create succeeds.</summary>
	public static FakeCloudflareHandler FreshAccount(FakeCloudflareHandler handler) => handler
		.On("GET", "accounts/a1/access/organizations", """{"name":"Jane","auth_domain":"jane.cloudflareaccess.com"}""")
		.On("GET", "accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50", "[]", totalPages: 0)
		.On("POST", "accounts/a1/cfd_tunnel", $$"""{"id":"{{TunnelId}}","name":"ai-chrome-proxy-homepc"}""")
		.On("GET", $"accounts/a1/cfd_tunnel/{TunnelId}/token", $"\"{TunnelToken}\"")
		.On("PUT", $"accounts/a1/cfd_tunnel/{TunnelId}/configurations", """{"tunnel_id":"t","version":1}""")
		.On("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", "[]", totalPages: 0)
		.On("POST", "zones/z1/dns_records", """{"id":"d1"}""")
		.On("GET", "accounts/a1/access/policies?page=1&per_page=50", """[{"id":"other","name":"Somebody else's"}]""", totalPages: 1)
		.On("POST", "accounts/a1/access/policies", $$"""{"id":"p1","name":"{{Policy}}"}""")
		.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"x","domain":"blog.example.com","aud":"other"}]""", totalPages: 1)
		.On("POST", "accounts/a1/access/apps", """{"id":"app1","domain":"code.example.com","aud":"aud-123"}""");

	[Fact]
	public async Task FreshAccount_CreatesEverything_InOrder_WithExactBodies()
	{
		FreshAccount(_handler);

		var result = await ProvisionAsync();

		Assert.Equal(new RemoteAccessResult("jane.cloudflareaccess.com", "aud-123", Host, TunnelToken), result);
		Assert.Equal(
			[
				"GET accounts/a1/access/organizations",
				"GET accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50",
				"POST accounts/a1/cfd_tunnel",
				$"GET accounts/a1/cfd_tunnel/{TunnelId}/token",
				$"PUT accounts/a1/cfd_tunnel/{TunnelId}/configurations",
				"GET zones/z1/dns_records?name=code.example.com&page=1&per_page=50",
				"POST zones/z1/dns_records",
				"GET accounts/a1/access/policies?page=1&per_page=50",
				"POST accounts/a1/access/policies",
				"GET accounts/a1/access/apps?page=1&per_page=50",
				"POST accounts/a1/access/apps",
			],
			_handler.Calls);
		AssertBody("POST", "accounts/a1/cfd_tunnel", """{"name":"ai-chrome-proxy-homepc","config_src":"cloudflare"}""");
		AssertBody(
			"PUT",
			$"accounts/a1/cfd_tunnel/{TunnelId}/configurations",
			"""{"config":{"ingress":[{"hostname":"code.example.com","service":"http://127.0.0.1:5180"},{"service":"http_status:404"}]}}""");
		AssertBody(
			"POST",
			"zones/z1/dns_records",
			$$"""{"type":"CNAME","name":"code.example.com","content":"{{TunnelId}}.cfargotunnel.com","proxied":true,"ttl":1}""");
		AssertBody(
			"POST",
			"accounts/a1/access/policies",
			$$$"""{"name":"{{{Policy}}}","decision":"allow","include":[{"email":{"email":"jane@example.com"}},{"email":{"email":"joe@example.com"}}]}""");
		AssertBody(
			"POST",
			"accounts/a1/access/apps",
			"""{"name":"AI Chrome Proxy","type":"self_hosted","domain":"code.example.com","session_duration":"24h","policies":[{"id":"p1","precedence":1}]}""");
		Assert.Equal(
			[
				"Zero Trust team domain: jane.cloudflareaccess.com",
				"Tunnel ai-chrome-proxy-homepc: created",
				"Tunnel route: code.example.com → http://127.0.0.1:5180",
				"DNS record code.example.com: CNAME created",
				$"Access policy {Policy}: created (jane@example.com, joe@example.com)",
				"Access application code.example.com: created",
			],
			_progress);
	}

	[Fact]
	public async Task SecondRun_ReusesEverything_UpdatesPolicyAndApp_NoDuplicates()
	{
		_handler
			.On("GET", "accounts/a1/access/organizations", """{"auth_domain":"jane.cloudflareaccess.com"}""")
			.On("GET", "accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50", $$"""[{"id":"{{TunnelId}}","name":"ai-chrome-proxy-homepc"}]""", totalPages: 1)
			.On("GET", $"accounts/a1/cfd_tunnel/{TunnelId}/token", $"\"{TunnelToken}\"")
			.On("PUT", $"accounts/a1/cfd_tunnel/{TunnelId}/configurations", "{}")
			.On("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", $$"""[{"id":"d1","type":"CNAME","content":"{{TunnelId}}.CFARGOTUNNEL.com","proxied":true}]""", totalPages: 1)
			.On("GET", "accounts/a1/access/policies?page=1&per_page=50", "[]", totalPages: 2)
			.On("GET", "accounts/a1/access/policies?page=2&per_page=50", $$"""[{"id":"p1","name":"{{Policy}}"}]""", totalPages: 2)
			.On("PUT", "accounts/a1/access/policies/p1", $$"""{"id":"p1","name":"{{Policy}}"}""")
			.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"app1","domain":"CODE.example.com","aud":"aud-old"}]""", totalPages: 1)
			.On("PUT", "accounts/a1/access/apps/app1", """{"id":"app1","domain":"code.example.com","aud":"aud-123"}""");

		var result = await ProvisionAsync("ann@example.org");

		Assert.Equal("aud-123", result.Audience);
		Assert.DoesNotContain(_handler.Requests, r => r.Method is "POST" or "PATCH");
		AssertBody("PUT", "accounts/a1/access/policies/p1", $$$"""{"name":"{{{Policy}}}","decision":"allow","include":[{"email":{"email":"ann@example.org"}}]}""");
		Assert.Equal(
			[
				"Zero Trust team domain: jane.cloudflareaccess.com",
				"Tunnel ai-chrome-proxy-homepc: reused",
				"Tunnel route: code.example.com → http://127.0.0.1:5180",
				"DNS record code.example.com: CNAME kept",
				$"Access policy {Policy}: updated (ann@example.org)",
				"Access application code.example.com: updated",
			],
			_progress);
	}

	[Fact]
	public async Task OurCnameUnproxied_PatchedToProxied()
	{
		FreshAccount(_handler)
			.On("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", $$"""[{"id":"d1","type":"CNAME","content":"{{TunnelId}}.cfargotunnel.com","proxied":false}]""", totalPages: 1)
			.On("PATCH", "zones/z1/dns_records/d1", """{"id":"d1"}""");

		await ProvisionAsync();

		AssertBody("PATCH", "zones/z1/dns_records/d1", """{"proxied":true}""");
		Assert.DoesNotContain("POST zones/z1/dns_records", _handler.Calls);
		Assert.Contains("DNS record code.example.com: CNAME switched to proxied", _progress);
	}

	[Theory]
	[InlineData("A", "203.0.113.10")]
	[InlineData("CNAME", "other.example.net")]
	public async Task ForeignDnsRecord_Refused_NothingAfterIt(string type, string content)
	{
		FreshAccount(_handler)
			.On("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", $$"""[{"id":"d9","type":"{{type}}","content":"{{content}}","proxied":true}]""", totalPages: 1);

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Equal("code.example.com already has a DNS record; choose another subdomain or delete it.", ex.Message);
		Assert.Equal("GET zones/z1/dns_records?name=code.example.com&page=1&per_page=50", _handler.Calls.Last());
		Assert.DoesNotContain(_handler.Requests, r => r.Path.StartsWith("zones/z1/dns_records", StringComparison.Ordinal) && r.Method != "GET");
	}

	[Fact]
	public async Task AccessNotEnabled_ClearError_NothingCreated()
	{
		FreshAccount(_handler)
			.OnError("GET", "accounts/a1/access/organizations", HttpStatusCode.NotFound, 12130, "access.api.error.not_enabled");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.StartsWith(
			"Cloudflare Access is not enabled for this account. Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry.",
			ex.Message,
			StringComparison.Ordinal);
		Assert.Contains("access.api.error.not_enabled", ex.Message, StringComparison.Ordinal);
		Assert.Single(_handler.Requests);
		Assert.Empty(_progress);
	}

	[Fact]
	public async Task OrganizationWithoutAuthDomain_SameClearError()
	{
		FreshAccount(_handler).On("GET", "accounts/a1/access/organizations", "{}");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Contains(RemoteAccessProvisioner.NotEnabledMessage, ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OrganizationServerError_NotReportedAsNotEnabled()
	{
		FreshAccount(_handler).OnError("GET", "accounts/a1/access/organizations", HttpStatusCode.InternalServerError, 10001, "Internal error");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Equal("Cloudflare API error 10001 on GET accounts/a1/access/organizations: Internal error", ex.Message);
	}

	[Fact]
	public void Names_FromMachineAndHost()
	{
		Assert.Equal("ai-chrome-proxy-homepc", RemoteAccessProvisioner.TunnelName("HOMEPC"));
		Assert.Equal(Policy, RemoteAccessProvisioner.PolicyName(Host));
		Assert.Equal(Host, new RemoteAccessRequest(Zone, "code", [], 5180, "x").PublicHost);
	}

	[Fact]
	public void Result_ToString_HidesTunnelToken()
	{
		var text = new RemoteAccessResult("t.cloudflareaccess.com", "aud", Host, TunnelToken).ToString();

		Assert.DoesNotContain(TunnelToken, text, StringComparison.Ordinal);
		Assert.Contains(Host, text, StringComparison.Ordinal);
	}

	public void Dispose() => _http.Dispose();

	private Task<RemoteAccessResult> ProvisionAsync(params string[] emails) =>
		new RemoteAccessProvisioner(new CloudflareApi(_http, "api-token")).ProvisionAsync(
			new RemoteAccessRequest(Zone, "code", emails.Length == 0 ? ["jane@example.com", "joe@example.com"] : emails, 5180, "HOMEPC"),
			new ListProgress(_progress),
			TestContext.Current.CancellationToken);

	private void AssertBody(string method, string path, string expectedJson)
	{
		var actual = _handler.Body(method, path);
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actual), actual?.ToJsonString());
	}

	/// <summary>Synchronous <see cref="IProgress{T}"/> (the BCL <c>Progress</c> posts to the thread pool).</summary>
	private sealed class ListProgress(List<string> lines) : IProgress<string>
	{
		public void Report(string value) => lines.Add(value);
	}
}
```

Note the `$$$"""…{{{Policy}}}…"""` literals: the JSON contains `}}`, so a `$$` raw string would not compile (CS9007).

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0246: The type or namespace name 'RemoteAccessResult' could not be found` (and the same for `RemoteAccessProvisioner`, `RemoteAccessRequest`).

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessRequest.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>What the wizard provisions: <c>https://&lt;Subdomain&gt;.&lt;Zone&gt;</c> for <paramref name="Emails"/>, tunnelled to <c>127.0.0.1:&lt;Port&gt;</c>.</summary>
/// <param name="MachineName">Names the tunnel (<c>ai-chrome-proxy-&lt;machine&gt;</c>), so each home server has its own.</param>
public sealed record RemoteAccessRequest(CloudflareZone Zone, string Subdomain, IReadOnlyList<string> Emails, int Port, string MachineName)
{
	public string PublicHost => $"{Subdomain}.{Zone.Name}";
}
```

Create `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessResult.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>The Server settings provisioning produced: <c>CloudflareAccess:TeamDomain</c>, <c>CloudflareAccess:Audience</c>, <c>Server:PublicHost</c>, <c>Tunnel:Token</c> (secret).</summary>
public sealed record RemoteAccessResult(string TeamDomain, string Audience, string PublicHost, string TunnelToken)
{
	/// <summary>Never prints the tunnel token.</summary>
	public override string ToString() => $"RemoteAccessResult {{ TeamDomain = {TeamDomain}, Audience = {Audience}, PublicHost = {PublicHost} }}";
}
```

Create `src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessProvisioner.cs`:

```csharp
using System.Text.Json;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>
/// Creates or reuses everything remote access needs: Cloudflare Tunnel (remotely managed) with its ingress, the proxied DNS CNAME,
/// the Access policy and the Access application. Every step finds before it creates, so a re-run converges instead of duplicating.
/// </summary>
public sealed class RemoteAccessProvisioner(CloudflareApi api)
{
	public const string ApplicationName = "AI Chrome Proxy";
	public const string NotEnabledMessage = "Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry.";

	public static string TunnelName(string machineName) => "ai-chrome-proxy-" + machineName.ToLowerInvariant();

	public static string PolicyName(string publicHost) => $"AI Chrome Proxy — {publicHost}";

	/// <summary>Runs the six steps in order, reporting one line per finished step.</summary>
	/// <exception cref="CloudflareApiException">A step failed (Access not enabled, a foreign DNS record, or an API error); later steps did not run.</exception>
	public async Task<RemoteAccessResult> ProvisionAsync(RemoteAccessRequest request, IProgress<string> progress, CancellationToken ct)
	{
		var account = request.Zone.Account.Id;
		var host = request.PublicHost;

		var teamDomain = await GetTeamDomainAsync(account, ct);
		progress.Report($"Zero Trust team domain: {teamDomain}");

		var (tunnel, tunnelCreated) = await FindOrCreateTunnelAsync(account, TunnelName(request.MachineName), ct);
		var tunnelToken = await api.SendAsync<string>(HttpMethod.Get, $"accounts/{account}/cfd_tunnel/{tunnel.Id}/token", null, ct);
		progress.Report($"Tunnel {tunnel.Name}: {(tunnelCreated ? "created" : "reused")}");

		var service = $"http://127.0.0.1:{request.Port}";
		var ingress = new { Config = new { Ingress = new object[] { new { Hostname = host, Service = service }, new { Service = "http_status:404" } } } };
		await api.SendAsync<JsonElement>(HttpMethod.Put, $"accounts/{account}/cfd_tunnel/{tunnel.Id}/configurations", ingress, ct);
		progress.Report($"Tunnel route: {host} → {service}");

		progress.Report($"DNS record {host}: {await EnsureCnameAsync(request.Zone.Id, host, tunnel.Id, ct)}");

		var (policy, policyCreated) = await UpsertPolicyAsync(account, PolicyName(host), request.Emails, ct);
		progress.Report($"Access policy {policy.Name}: {(policyCreated ? "created" : "updated")} ({string.Join(", ", request.Emails)})");

		var (app, appCreated) = await UpsertApplicationAsync(account, host, policy.Id, ct);
		progress.Report($"Access application {host}: {(appCreated ? "created" : "updated")}");

		return new RemoteAccessResult(teamDomain, app.Aud, host, tunnelToken);
	}

	private async Task<string> GetTeamDomainAsync(string account, CancellationToken ct)
	{
		Organization organization;
		try
		{
			organization = await api.SendAsync<Organization>(HttpMethod.Get, $"accounts/{account}/access/organizations", null, ct);
		}
		catch (CloudflareApiException ex) when ((int?)ex.StatusCode is >= 400 and < 500 and not 429)
		{
			throw new CloudflareApiException($"Cloudflare Access is not enabled for this account. {NotEnabledMessage} ({ex.Message})", ex.Code, ex.StatusCode);
		}

		return string.IsNullOrWhiteSpace(organization.AuthDomain)
			? throw new CloudflareApiException($"Cloudflare Access is not enabled for this account. {NotEnabledMessage}")
			: organization.AuthDomain;
	}

	private async Task<(Tunnel Tunnel, bool Created)> FindOrCreateTunnelAsync(string account, string name, CancellationToken ct)
	{
		var tunnels = await api.ListAsync<Tunnel>($"accounts/{account}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false", ct);
		if (tunnels.FirstOrDefault(t => t.Name == name) is { } existing)
		{
			return (existing, false);
		}

		var created = await api.SendAsync<Tunnel>(HttpMethod.Post, $"accounts/{account}/cfd_tunnel", new { Name = name, ConfigSrc = "cloudflare" }, ct);
		return (created, true);
	}

	/// <returns>What happened, for the progress line.</returns>
	private async Task<string> EnsureCnameAsync(string zone, string host, string tunnelId, CancellationToken ct)
	{
		var target = $"{tunnelId}.cfargotunnel.com";
		var records = await api.ListAsync<DnsRecord>($"zones/{zone}/dns_records?name={Uri.EscapeDataString(host)}", ct);
		bool IsOurs(DnsRecord r) => r.Type == "CNAME" && string.Equals(r.Content, target, StringComparison.OrdinalIgnoreCase);

		if (records.Any(r => !IsOurs(r)))
		{
			throw new CloudflareApiException($"{host} already has a DNS record; choose another subdomain or delete it.");
		}

		if (records.FirstOrDefault() is not { } ours)
		{
			var record = new { Type = "CNAME", Name = host, Content = target, Proxied = true, Ttl = 1 };
			await api.SendAsync<JsonElement>(HttpMethod.Post, $"zones/{zone}/dns_records", record, ct);
			return "CNAME created";
		}

		if (ours.Proxied)
		{
			return "CNAME kept";
		}

		await api.SendAsync<JsonElement>(HttpMethod.Patch, $"zones/{zone}/dns_records/{ours.Id}", new { Proxied = true }, ct);
		return "CNAME switched to proxied";
	}

	private async Task<(Policy Policy, bool Created)> UpsertPolicyAsync(string account, string name, IReadOnlyList<string> emails, CancellationToken ct)
	{
		var body = new { Name = name, Decision = "allow", Include = emails.Select(e => new { Email = new { Email = e } }).ToList() };
		var policies = await api.ListAsync<Policy>($"accounts/{account}/access/policies", ct);
		if (policies.FirstOrDefault(p => p.Name == name) is { } existing)
		{
			return (await api.SendAsync<Policy>(HttpMethod.Put, $"accounts/{account}/access/policies/{existing.Id}", body, ct), false);
		}

		return (await api.SendAsync<Policy>(HttpMethod.Post, $"accounts/{account}/access/policies", body, ct), true);
	}

	private async Task<(Application Application, bool Created)> UpsertApplicationAsync(string account, string host, string policyId, CancellationToken ct)
	{
		var body = new
		{
			Name = ApplicationName,
			Type = "self_hosted",
			Domain = host,
			SessionDuration = "24h",
			Policies = new[] { new { Id = policyId, Precedence = 1 } },
		};
		var apps = await api.ListAsync<Application>($"accounts/{account}/access/apps", ct);
		if (apps.FirstOrDefault(a => string.Equals(a.Domain, host, StringComparison.OrdinalIgnoreCase)) is { } existing)
		{
			return (await api.SendAsync<Application>(HttpMethod.Put, $"accounts/{account}/access/apps/{existing.Id}", body, ct), false);
		}

		return (await api.SendAsync<Application>(HttpMethod.Post, $"accounts/{account}/access/apps", body, ct), true);
	}

	private sealed record Organization(string? AuthDomain);

	private sealed record Tunnel(string Id, string Name);

	private sealed record DnsRecord(string Id, string Type, string Content, bool Proxied);

	private sealed record Policy(string Id, string Name);

	private sealed record Application(string Id, string? Domain, string Aud);
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Infrastructure.RemoteAccessProvisionerTests"` → `total: 10`, `failed: 0`.
Run the gate → `total: 304`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessRequest.cs src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessResult.cs src/AiChromeProxy.Infrastructure/Cloudflare/RemoteAccessProvisioner.cs tests/AiChromeProxy.Tests/Infrastructure/RemoteAccessProvisionerTests.cs
git status --short
git commit -m "feat(infra): idempotent remote access provisioning (tunnel, DNS, Access policy and app)"
```

---

### Task 6: `CloudflaredSupervisor` in the Server (process seam + Job object)

**Files:**
- Create: `src/AiChromeProxy.Server/Hosting/ICloudflaredProcess.cs`, `src/AiChromeProxy.Server/Hosting/CloudflaredSupervisor.cs`, `src/AiChromeProxy.Server/Hosting/CloudflaredProcess.cs`
- Modify: `src/AiChromeProxy.Server/Program.cs` (whole file)
- Create: `tests/AiChromeProxy.Tests/Server/ListLogger.cs`, `tests/AiChromeProxy.Tests/Server/CloudflaredSupervisorTests.cs`
- Modify: `tests/AiChromeProxy.Tests/Server/ServerHostingTests.cs`

**Interfaces:**
- Consumes: `TunnelOptions` (Task 3); `TimeProvider` registered by `AddInfrastructure` (existing `TryAddSingleton(TimeProvider.System)`).
- Produces (namespace `AiChromeProxy.Server.Hosting`):
  - `public interface ICloudflaredProcess : IDisposable { Task<int> WaitForExitAsync(CancellationToken ct); void Kill(); }`
  - `public sealed class CloudflaredSupervisor(TunnelOptions options, ILogger<CloudflaredSupervisor> logger, TimeProvider time, Func<ProcessStartInfo, Action<string>, ICloudflaredProcess> start) : BackgroundService` with `const string TokenVariable = "TUNNEL_TOKEN"`, `const string BundledFileName = "cloudflared.exe"`, `static readonly TimeSpan FirstBackoff` (1 s), `MaxBackoff` (60 s), `StableRun` (5 min), `static string ResolveExecutable(string? configuredPath, string baseDirectory)`, `Task RunAsync(CancellationToken ct)`.
  - `[ExcludeFromCodeCoverage] public sealed class CloudflaredProcess : ICloudflaredProcess` with `static ICloudflaredProcess Start(ProcessStartInfo info, Action<string> output)`.
- Log lines (the tray's Logs window shows them): `cloudflared: {Line}` (Information), `cloudflared started ({Path}).`, `cloudflared exited with code {ExitCode}.` (Warning), `cloudflared could not be started ({Path}).` (Error), `Restarting cloudflared in {Delay}.`, `Cloudflare Tunnel not configured (Tunnel:Token is empty); cloudflared is not started.`

Testing approach (deterministic, no sleeps on the fake clock): `RunAsync` is called directly, so its synchronous prefix (start → first `await`) has run when it returns; the supervisor awaits with `ConfigureAwait(false)`; a `FakeTimeProvider` subclass counts created timers so a test advances the clock only once the backoff wait is armed; `WaitUntilAsync` polls (≤ 10 s) for the restart that follows.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Server/ListLogger.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Server;

/// <summary>Records formatted log messages; thread-safe (background services log from the thread pool).</summary>
public sealed class ListLogger<T> : ILogger<T>
{
	private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

	public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

	public IReadOnlyList<string> Messages => [.. _entries.Select(e => e.Message)];

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
		_entries.Enqueue((logLevel, formatter(state, exception)));
}
```

Create `tests/AiChromeProxy.Tests/Server/CloudflaredSupervisorTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Server;

/// <summary>The supervision loop against a fake process and a fake clock: no real <c>cloudflared</c> is ever started.</summary>
public sealed class CloudflaredSupervisorTests
{
	private const string Token = "tunnel-token-secret";
	private const string ConfiguredPath = @"C:\tools\cloudflared.exe";

	private readonly CountingTimeProvider _time = new();
	private readonly ListLogger<CloudflaredSupervisor> _logger = new();
	private readonly ConcurrentQueue<ProcessStartInfo> _starts = new();
	private readonly ConcurrentQueue<FakeProcess> _processes = new();
	private Action<string>? _output;

	private Exception? FailStart { get; set; }

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	public async Task NoToken_NeverStarts_LogsOnce(string token)
	{
		await Create(token).RunAsync(TestContext.Current.CancellationToken);

		Assert.Empty(_starts);
		Assert.Equal(["Cloudflare Tunnel not configured (Tunnel:Token is empty); cloudflared is not started."], _logger.Messages);
	}

	[Fact]
	public async Task Start_TokenOnlyInChildEnvironment_OutputLogged()
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var run = Create().RunAsync(cts.Token);

		var info = Assert.Single(_starts);
		Assert.Equal(ConfiguredPath, info.FileName);
		Assert.Equal(["tunnel", "--no-autoupdate", "run"], info.ArgumentList);
		Assert.Equal(string.Empty, info.Arguments);
		Assert.Equal(Token, info.Environment[CloudflaredSupervisor.TokenVariable]);

		_output!("INF Registered tunnel connection");
		Assert.Contains("cloudflared: INF Registered tunnel connection", _logger.Messages);
		Assert.DoesNotContain(_logger.Messages, m => m.Contains(Token, StringComparison.Ordinal));

		await cts.CancelAsync();
		await run;
	}

	[Fact]
	public async Task Exits_RestartedWithDoublingBackoff_CappedAt60s()
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var run = Create().RunAsync(cts.Token);

		foreach (var seconds in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
		{
			await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(seconds));
		}

		Assert.Contains(_logger.Entries, e => e is (LogLevel.Warning, "cloudflared exited with code 1."));
		Assert.Contains("Restarting cloudflared in 00:01:00.", _logger.Messages);
		await cts.CancelAsync();
		await run;
	}

	[Fact]
	public async Task RunOfFiveMinutes_ResetsBackoff()
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var run = Create().RunAsync(cts.Token);
		await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(1));
		await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(2));

		_time.Advance(CloudflaredSupervisor.StableRun);

		await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(1));
		await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(2));
		await cts.CancelAsync();
		await run;
	}

	[Fact]
	public async Task StartFailure_LoggedAsError_RetriedWithBackoff()
	{
		FailStart = new Win32Exception(2, "The system cannot find the file specified");
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var run = Create().RunAsync(cts.Token);

		await WaitUntilAsync(() => _time.Timers == 1);
		_time.Advance(TimeSpan.FromSeconds(1));
		await WaitUntilAsync(() => _time.Timers == 2);
		FailStart = null;
		_time.Advance(TimeSpan.FromSeconds(2));
		await WaitUntilAsync(() => _processes.Count == 1);

		Assert.Equal(3, _starts.Count);
		Assert.Equal(2, _logger.Entries.Count(e => e is (LogLevel.Error, $"cloudflared could not be started ({ConfiguredPath}).")));
		await cts.CancelAsync();
		await run;
	}

	[Fact]
	public async Task Shutdown_KillsAndDisposesTheProcess_NoRestart()
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var run = Create().RunAsync(cts.Token);

		await cts.CancelAsync();
		await run;

		var process = Assert.Single(_processes);
		Assert.True(process.Killed);
		Assert.True(process.Disposed);
		Assert.Single(_starts);
	}

	[Fact]
	public async Task Shutdown_DuringBackoff_Returns_NoRestart()
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var run = Create().RunAsync(cts.Token);
		_processes.Last().Exit(0);
		await WaitUntilAsync(() => _time.Timers == 1);

		await cts.CancelAsync();
		await run;

		Assert.Single(_starts);
		Assert.False(_processes.Last().Killed);
	}

	[Fact]
	public async Task HostedService_StartAndStop_KillsTheProcess()
	{
		using var supervisor = Create();

		await supervisor.StartAsync(TestContext.Current.CancellationToken);
		await WaitUntilAsync(() => _processes.Count == 1);
		await supervisor.StopAsync(TestContext.Current.CancellationToken);

		Assert.True(_processes.Last().Killed);
	}

	[Fact]
	public void ResolveExecutable_ConfiguredThenBundledThenPath()
	{
		var dir = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			Assert.Equal(ConfiguredPath, CloudflaredSupervisor.ResolveExecutable(ConfiguredPath, dir));
			Assert.Equal("cloudflared", CloudflaredSupervisor.ResolveExecutable(" ", dir));

			var bundled = Path.Combine(dir, CloudflaredSupervisor.BundledFileName);
			File.WriteAllText(bundled, string.Empty);

			Assert.Equal(bundled, CloudflaredSupervisor.ResolveExecutable(null, dir));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the supervisor");
			await Task.Delay(5, TestContext.Current.CancellationToken);
		}
	}

	private CloudflaredSupervisor Create(string token = Token) =>
		new(new TunnelOptions { Token = token, CloudflaredPath = ConfiguredPath }, _logger, _time, Start);

	private ICloudflaredProcess Start(ProcessStartInfo info, Action<string> output)
	{
		_starts.Enqueue(info);
		_output = output;
		if (FailStart is { } failure)
		{
			throw failure;
		}

		var process = new FakeProcess();
		_processes.Enqueue(process);
		return process;
	}

	/// <summary>Exits the running process, waits until the supervisor armed its backoff timer, then checks the restart comes exactly after <paramref name="backoff"/>.</summary>
	private async Task ExitAndExpectRestartAfterAsync(TimeSpan backoff)
	{
		var timers = _time.Timers;
		var started = _processes.Count;
		_processes.Last().Exit(1);
		await WaitUntilAsync(() => _time.Timers == timers + 1);

		_time.Advance(backoff - TimeSpan.FromMilliseconds(1));
		Assert.Equal(started, _processes.Count);
		_time.Advance(TimeSpan.FromMilliseconds(1));
		await WaitUntilAsync(() => _processes.Count == started + 1);
	}

	/// <summary>Counts timers (each backoff wait creates one), so a test advances the clock only once the wait is armed.</summary>
	private sealed class CountingTimeProvider : FakeTimeProvider
	{
		private int _timers;

		public int Timers => Volatile.Read(ref _timers);

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = base.CreateTimer(callback, state, dueTime, period);
			Interlocked.Increment(ref _timers);
			return timer;
		}
	}

	private sealed class FakeProcess : ICloudflaredProcess
	{
		private readonly TaskCompletionSource<int> _exit = new();

		public bool Killed { get; private set; }

		public bool Disposed { get; private set; }

		public void Exit(int exitCode) => _exit.SetResult(exitCode);

		public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

		public void Kill()
		{
			Killed = true;
			_exit.TrySetResult(-1);
		}

		public void Dispose() => Disposed = true;
	}
}
```

In `tests/AiChromeProxy.Tests/Server/ServerHostingTests.cs`:
1. add `using AiChromeProxy.Server.Hosting;` after `using AiChromeProxy.Infrastructure.Security;`;
2. in the constructor, make the first setting of the factory `.UseSetting("Tunnel:Token", string.Empty)` (a real `Tunnel__Token` on the test machine must never start `cloudflared`):

```csharp
		_factory = Factory(b => b
			.UseSetting("Tunnel:Token", string.Empty)
			.UseSetting("Server:PublicHost", PublicHost)
```

3. add this test above `Production_WithoutPublicHost_FailsToStart`:

```csharp
	[Fact]
	public void CloudflaredSupervisor_Registered()
	{
		Assert.Single(_factory.Services.GetServices<IHostedService>().OfType<CloudflaredSupervisor>());
	}

```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0246: The type or namespace name 'CloudflaredSupervisor' could not be found` (and `ICloudflaredProcess`).

- [ ] **Step 3: Implement the seam, the supervisor and the real process**

Create `src/AiChromeProxy.Server/Hosting/ICloudflaredProcess.cs`:

```csharp
namespace AiChromeProxy.Server.Hosting;

/// <summary>A started <c>cloudflared</c>: the seam that keeps <see cref="CloudflaredSupervisor"/> testable without a real process.</summary>
public interface ICloudflaredProcess : IDisposable
{
	/// <returns>The exit code, once the process has exited.</returns>
	Task<int> WaitForExitAsync(CancellationToken ct);

	/// <summary>Kills the process and its children; no-op once it has exited.</summary>
	void Kill();
}
```

Create `src/AiChromeProxy.Server/Hosting/CloudflaredSupervisor.cs`:

```csharp
using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// Runs <c>cloudflared tunnel --no-autoupdate run</c> as a child of the Server while <c>Tunnel:Token</c> is set, restarting it with
/// exponential backoff. The token reaches the child only through its <c>TUNNEL_TOKEN</c> environment variable: never a command line or a log.
/// </summary>
/// <param name="start">Starts the process and sends each stdout/stderr line to the callback (<see cref="CloudflaredProcess.Start"/>; a fake in tests).</param>
public sealed class CloudflaredSupervisor(
	TunnelOptions options,
	ILogger<CloudflaredSupervisor> logger,
	TimeProvider time,
	Func<ProcessStartInfo, Action<string>, ICloudflaredProcess> start) : BackgroundService
{
	public const string TokenVariable = "TUNNEL_TOKEN";
	public const string BundledFileName = "cloudflared.exe";

	public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
	public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

	/// <summary>A run at least this long counts as healthy: the next restart waits <see cref="FirstBackoff"/> again.</summary>
	public static readonly TimeSpan StableRun = TimeSpan.FromMinutes(5);

	/// <summary><c>Tunnel:CloudflaredPath</c>, else the bundled <c>cloudflared.exe</c> next to the Server, else <c>cloudflared</c> from PATH.</summary>
	public static string ResolveExecutable(string? configuredPath, string baseDirectory)
	{
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return configuredPath;
		}

		var bundled = Path.Combine(baseDirectory, BundledFileName);
		return File.Exists(bundled) ? bundled : "cloudflared";
	}

	/// <summary>The supervision loop; returns when <paramref name="ct"/> is cancelled (the running process is killed) or no token is configured.</summary>
	public async Task RunAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(options.Token))
		{
			logger.LogInformation("Cloudflare Tunnel not configured (Tunnel:Token is empty); cloudflared is not started.");
			return;
		}

		var backoff = FirstBackoff;
		while (true)
		{
			var started = time.GetTimestamp();
			var info = StartInfo();
			ICloudflaredProcess? process = null;
			try
			{
				process = start(info, line => logger.LogInformation("cloudflared: {Line}", line));
				logger.LogInformation("cloudflared started ({Path}).", info.FileName);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "cloudflared could not be started ({Path}).", info.FileName);
			}

			if (process is not null)
			{
				using (process)
				{
					try
					{
						var exitCode = await process.WaitForExitAsync(ct).ConfigureAwait(false);
						logger.LogWarning("cloudflared exited with code {ExitCode}.", exitCode);
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested)
					{
						process.Kill();
						return;
					}
				}
			}

			if (time.GetElapsedTime(started) >= StableRun)
			{
				backoff = FirstBackoff;
			}

			logger.LogInformation("Restarting cloudflared in {Delay}.", backoff);
			try
			{
				await Task.Delay(backoff, time, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			backoff = backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
		}
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

	private ProcessStartInfo StartInfo()
	{
		var info = new ProcessStartInfo(ResolveExecutable(options.CloudflaredPath, AppContext.BaseDirectory)) { ArgumentList = { "tunnel", "--no-autoupdate", "run" } };
		info.Environment[TokenVariable] = options.Token;
		return info;
	}
}
```

Create `src/AiChromeProxy.Server/Hosting/CloudflaredProcess.cs` (never run by tests; the manual checklist in Task 10 covers it):

```csharp
#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute: DllImport keeps the Server free of unsafe code.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// The real <c>cloudflared</c> child process. On Windows it is put in a Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the
/// job handle lives as long as the Server process, so even a crashed or killed Server leaves no orphan <c>cloudflared</c> holding the tunnel.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Process and Win32 Job object glue; the decisions are in CloudflaredSupervisor, tested through a fake.")]
public sealed class CloudflaredProcess : ICloudflaredProcess
{
	private const int JobObjectExtendedLimitInformationClass = 9;
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	/// <summary>One job per Server process, never closed explicitly: Windows closes it when the Server exits, which kills the children.</summary>
	private static readonly Lazy<SafeFileHandle?> Job = new(CreateKillOnCloseJob);

	private readonly Process _process;

	private CloudflaredProcess(Process process) => _process = process;

	/// <summary>Starts <paramref name="info"/> with redirected output (each line to <paramref name="output"/>) and no window.</summary>
	/// <exception cref="Win32Exception">The executable was not found or could not be started.</exception>
	public static ICloudflaredProcess Start(ProcessStartInfo info, Action<string> output)
	{
		info.UseShellExecute = false;
		info.CreateNoWindow = true;
		info.RedirectStandardOutput = true;
		info.RedirectStandardError = true;
		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		process.OutputDataReceived += (_, e) => Forward(e.Data, output);
		process.ErrorDataReceived += (_, e) => Forward(e.Data, output);
		process.Start();

		if (OperatingSystem.IsWindows() && Job.Value is { } job && !AssignProcessToJobObject(job, process.SafeHandle))
		{
			var error = Marshal.GetLastPInvokeError();
			process.Kill(entireProcessTree: true);
			process.Dispose();
			throw new Win32Exception(error, "Could not put cloudflared in the Server's job object.");
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return new CloudflaredProcess(process);
	}

	public async Task<int> WaitForExitAsync(CancellationToken ct)
	{
		await _process.WaitForExitAsync(ct);
		return _process.ExitCode;
	}

	public void Kill()
	{
		try
		{
			_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited.
		}
	}

	public void Dispose() => _process.Dispose();

	private static void Forward(string? line, Action<string> output)
	{
		if (!string.IsNullOrEmpty(line))
		{
			output(line);
		}
	}

	private static SafeFileHandle? CreateKillOnCloseJob()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		var job = CreateJobObjectW(IntPtr.Zero, null);
		if (job.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a job object for cloudflared.");
		}

		var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
		if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
		{
			var error = Marshal.GetLastPInvokeError();
			job.Dispose();
			throw new Win32Exception(error, "Could not configure the job object for cloudflared.");
		}

		return job;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectExtendedLimitInformation
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}
}
```

Replace the whole content of `src/AiChromeProxy.Server/Program.cs` with (only the `TunnelOptions` + `AddHostedService` lines after `AddInfrastructure` are new):

```csharp
using System.Net;
using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Hosting;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

var isService = WindowsServiceHelpers.IsWindowsService();

// A service starts in %WINDIR%\System32: content root (appsettings.json, wwwroot) must be the exe folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	ContentRootPath = isService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService();

var dataDir = DataDirectoryHosting.Select(isService, Environment.GetEnvironmentVariable(DataDirectory.OverrideVariable));
if (dataDir is not null)
{
	builder.Configuration.AddPersistentSettings(dataDir);
}

builder.Services.AddServerLogging(builder.Configuration, dataDir);

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, server.Port));
builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = [.. server.AllowedHosts()]);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<TunnelOptions>(builder.Configuration.GetSection(TunnelOptions.Section));
builder.Services.AddHostedService(sp => new CloudflaredSupervisor(
	sp.GetRequiredService<IOptions<TunnelOptions>>().Value,
	sp.GetRequiredService<ILogger<CloudflaredSupervisor>>(),
	sp.GetRequiredService<TimeProvider>(),
	CloudflaredProcess.Start));
builder.Services.AddSignalR();

var app = builder.Build();

var access = app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value;
try
{
	server.Validate(app.Environment);
	access.Validate(app.Environment);
}
catch (InvalidOperationException ex)
{
	// A service has no console: the log file is the only place this reason shows up.
	app.Logger.LogCritical(ex, "Invalid configuration, the Server will not start: {Reason}", ex.Message);
	throw;
}

if (!access.Enabled)
{
	app.Logger.LogWarning("Cloudflare Access check is DISABLED (Development only). Do not expose this server.");
}

app.UseMiddleware<CloudflareAccessMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapHub<TransportHub>(TransportHub.Path);
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point; partial so WebApplicationFactory can reference it.</summary>
public partial class Program
{
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Server.CloudflaredSupervisorTests"` → `total: 10`, `failed: 0`. Run it 5 times; it must pass every time (it is deterministic: no real time is waited on the fake clock).
Run the gate → `total: 315`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Server/Hosting/ICloudflaredProcess.cs src/AiChromeProxy.Server/Hosting/CloudflaredSupervisor.cs src/AiChromeProxy.Server/Hosting/CloudflaredProcess.cs src/AiChromeProxy.Server/Program.cs tests/AiChromeProxy.Tests/Server/ListLogger.cs tests/AiChromeProxy.Tests/Server/CloudflaredSupervisorTests.cs tests/AiChromeProxy.Tests/Server/ServerHostingTests.cs
git status --short
git commit -m "feat(server): run and supervise cloudflared (backoff, kill-on-close job object)"
```

---

### Task 7: `RemoteAccessViewModel` (the wizard's logic)

**Files:**
- Create: `src/AiChromeProxy.Tray/ViewModels/RemoteAccessViewModel.cs`
- Create: `tests/AiChromeProxy.Tests/Tray/RemoteAccessViewModelTests.cs`

**Interfaces:**
- Consumes: `CloudflareApi`, `CloudflareZone` (Task 3); `RemoteAccessInput` (Task 4); `RemoteAccessProvisioner`, `RemoteAccessRequest` (Task 5); `SettingsFile` (Task 2); `TunnelOptions` (Task 3); `IServiceControl`, `ServiceState`, `AdminCommand.Install`, `AdminCommand.Cancelled` (existing); tests: `FakeCloudflareHandler`, `RemoteAccessProvisionerTests.FreshAccount/TunnelId/TunnelToken`, `FakeServiceControl`.
- Produces (namespace `AiChromeProxy.Tray.ViewModels`): `public sealed partial class RemoteAccessViewModel(DataDirectory dataDir, HttpClient http, IServiceControl service, Func<string, Task<int?>> runElevated, string machineName) : ObservableObject` with
  - `const string CreateTokenUrl`; nested `enum WizardStage { Token, Details, Progress }`; `static bool NeedsSetup(DataDirectory dataDir)`;
  - bindable: `Stage`, `IsTokenStage`, `IsDetailsStage`, `IsProgressStage`, `ApiToken`, `Zones`, `SelectedZone`, `Subdomain` (default `code`), `Emails`, `PublicUrl`, `Errors`, `Steps`, `IsBusy`, `Failed`, `Succeeded`, `Status`, `IsInstallOffered`, `IsRestartOffered`, `CanGoBack`;
  - commands: `ContinueCommand`, `SetUpCommand`, `BackCommand`, `InstallServiceCommand`, `RestartServiceCommand`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Tray/RemoteAccessViewModelTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tests.Infrastructure;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using static AiChromeProxy.Tray.ViewModels.RemoteAccessViewModel;

namespace AiChromeProxy.Tests.Tray;

public sealed class RemoteAccessViewModelTests : IDisposable
{
	private const string Zones = """[{"id":"z1","name":"example.com","account":{"id":"a1","name":"Jane"}},{"id":"z2","name":"example.org","account":{"id":"a1","name":"Jane"}}]""";

	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeCloudflareHandler _handler = new();
	private readonly HttpClient _http;
	private readonly FakeServiceControl _service = new(ServiceState.Running);
	private readonly List<string> _elevated = [];
	private int? _elevatedExitCode = 0;

	public RemoteAccessViewModelTests()
	{
		_http = new HttpClient(_handler);
		_handler
			.On("GET", "user/tokens/verify", """{"id":"t","status":"active"}""")
			.On("GET", "zones?status=active&page=1&per_page=50", Zones, totalPages: 1);
	}

	[Fact]
	public void Defaults_TokenStage_CodeSubdomain()
	{
		var vm = Create();

		Assert.True(vm.IsTokenStage);
		Assert.False(vm.IsDetailsStage || vm.IsProgressStage);
		Assert.Equal("code", vm.Subdomain);
		Assert.Equal(string.Empty, vm.PublicUrl);
		Assert.False(vm.BackCommand.CanExecute(null));
	}

	[Fact]
	public async Task Continue_NoToken_Error_NoRequest()
	{
		var vm = Create();
		vm.ApiToken = " ";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Equal(["Paste a Cloudflare API token."], vm.Errors);
		Assert.Empty(_handler.Requests);
		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task Continue_ValidToken_DetailsWithZones_PublicUrlFollowsInput()
	{
		var vm = Create();
		vm.ApiToken = " api-token ";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Empty(vm.Errors);
		Assert.True(vm.IsDetailsStage);
		Assert.Equal(["example.com", "example.org"], vm.Zones.Select(z => z.Name));
		Assert.Equal("Bearer api-token", _handler.Requests[0].Authorization);
		Assert.Equal("https://code.example.com/", vm.PublicUrl);

		vm.Subdomain = " Dev ";
		vm.SelectedZone = vm.Zones[1];

		Assert.Equal("https://dev.example.org/", vm.PublicUrl);
	}

	[Fact]
	public async Task Continue_RejectedToken_ErrorShown_StaysOnTokenStage()
	{
		_handler.OnError("GET", "user/tokens/verify", HttpStatusCode.Unauthorized, 1000, "Invalid API Token");
		var vm = Create();
		vm.ApiToken = "bad";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Equal(["Cloudflare API error 1000 on GET user/tokens/verify: Invalid API Token"], vm.Errors);
		Assert.True(vm.IsTokenStage);
		Assert.False(vm.IsBusy);
	}

	[Fact]
	public async Task Continue_NoZones_ExplainsPermissions()
	{
		_handler.On("GET", "zones?status=active&page=1&per_page=50", "[]", totalPages: 0);
		var vm = Create();
		vm.ApiToken = "t";

		await vm.ContinueCommand.ExecuteAsync(null);

		Assert.Contains("no active zone", Assert.Single(vm.Errors), StringComparison.Ordinal);
		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task Back_FromDetails_ToToken()
	{
		var vm = await DetailsAsync();

		vm.BackCommand.Execute(null);

		Assert.True(vm.IsTokenStage);
	}

	[Fact]
	public async Task SetUp_InvalidDetails_InlineErrors_NothingProvisioned()
	{
		var vm = await DetailsAsync();
		vm.Subdomain = "-bad";
		vm.Emails = "jane";
		var requests = _handler.Requests.Count;

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Equal(
			["The subdomain must be one label of a-z, 0-9 and '-', 1 to 63 characters, not starting or ending with '-'.", "'jane' is not an email address."],
			vm.Errors);
		Assert.True(vm.IsDetailsStage);
		Assert.Equal(requests, _handler.Requests.Count);
	}

	[Fact]
	public async Task SetUp_Success_WritesTheFourValues_KeepsOtherKeys_ClearsApiToken_OffersRestart()
	{
		WriteSettings("""{ "Server": { "Port": 6000 }, "Serilog": { "MinimumLevel": "Debug" } }""");
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com,\njoe@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Empty(vm.Errors);
		Assert.True(vm.Succeeded);
		Assert.True(vm.IsProgressStage);
		Assert.Equal(string.Empty, vm.ApiToken);
		Assert.Equal(7, vm.Steps.Count);
		Assert.Equal($"Settings saved to {_dataDir.SettingsFile}", vm.Steps[^1]);
		Assert.Equal("http://127.0.0.1:6000", (string?)_handler.Body("PUT", $"accounts/a1/cfd_tunnel/{RemoteAccessProvisionerTests.TunnelId}/configurations")!["config"]!["ingress"]![0]!["service"]);
		var expected = JsonNode.Parse($$"""
			{
			  "Server": { "Port": 6000, "PublicHost": "code.example.com" },
			  "Serilog": { "MinimumLevel": "Debug" },
			  "CloudflareAccess": { "TeamDomain": "jane.cloudflareaccess.com", "Audience": "aud-123" },
			  "Tunnel": { "Token": "{{RemoteAccessProvisionerTests.TunnelToken}}" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadSettings()), ReadSettings().ToJsonString());
		Assert.True(vm.IsRestartOffered);
		Assert.False(vm.IsInstallOffered);
		Assert.Equal("Remote access is set up. Restart the service to apply.", vm.Status);
		Assert.False(vm.BackCommand.CanExecute(null));
	}

	[Fact]
	public async Task Restart_StopsThenStarts()
	{
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["stop", "start"], _service.Calls);
		Assert.False(vm.IsRestartOffered);
		Assert.Equal("Service restarted: remote access is live.", vm.Status);
	}

	[Fact]
	public async Task Restart_Fails_ErrorShown()
	{
		_service.FailStart = new InvalidOperationException("cannot start");
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["cannot start"], vm.Errors);
	}

	[Fact]
	public async Task SetUp_Success_NoService_OffersInstall_RunsElevatedInstall()
	{
		_service.State = ServiceState.NotInstalled;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);
		Assert.True(vm.IsInstallOffered);
		Assert.Equal("Remote access is set up. Install the service to start it.", vm.Status);

		_service.State = ServiceState.Running;
		await vm.InstallServiceCommand.ExecuteAsync(null);

		Assert.Equal([AdminCommand.Install], _elevated);
		Assert.False(vm.IsInstallOffered);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public async Task Install_FailedExitCode_ErrorShown()
	{
		_service.State = ServiceState.NotInstalled;
		_elevatedExitCode = 5;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";
		await vm.SetUpCommand.ExecuteAsync(null);

		await vm.InstallServiceCommand.ExecuteAsync(null);

		Assert.Equal(["Service install did not complete (exit code 5)."], vm.Errors);
		Assert.True(vm.IsInstallOffered);
	}

	[Fact]
	public async Task SetUp_Success_ServiceStopped_AppliesAtNextStart()
	{
		_service.State = ServiceState.Stopped;
		RemoteAccessProvisionerTests.FreshAccount(_handler);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.False(vm.IsInstallOffered || vm.IsRestartOffered);
		Assert.Equal("Remote access is set up. It applies when the service starts.", vm.Status);
	}

	[Fact]
	public async Task SetUp_ProvisioningFails_ErrorAndBack_NoSettingsWritten()
	{
		RemoteAccessProvisionerTests.FreshAccount(_handler)
			.On("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", """[{"id":"d9","type":"A","content":"203.0.113.10","proxied":true}]""", totalPages: 1);
		var vm = await DetailsAsync();
		vm.Emails = "jane@example.com";

		await vm.SetUpCommand.ExecuteAsync(null);

		Assert.Equal(["code.example.com already has a DNS record; choose another subdomain or delete it."], vm.Errors);
		Assert.True(vm.Failed);
		Assert.False(vm.Succeeded);
		Assert.Equal(3, vm.Steps.Count);
		Assert.False(File.Exists(_dataDir.SettingsFile));
		Assert.Equal("api-token", vm.ApiToken);

		vm.BackCommand.Execute(null);

		Assert.True(vm.IsDetailsStage);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public void NeedsSetup_OnlyWithoutPublicHost()
	{
		Assert.True(NeedsSetup(_dataDir));

		WriteSettings("""{ "Server": { "Port": 5180 } }""");
		Assert.True(NeedsSetup(_dataDir));

		WriteSettings("{ broken");
		Assert.True(NeedsSetup(_dataDir));

		WriteSettings("""{ "Server": { "PublicHost": "code.example.com" } }""");
		Assert.False(NeedsSetup(_dataDir));
	}

	[Fact]
	public void NeedsSetup_UnreadableFile_False()
	{
		WriteSettings("{}");
		using var locked = File.Open(_dataDir.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		Assert.False(NeedsSetup(_dataDir));
	}

	public void Dispose()
	{
		_http.Dispose();
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private RemoteAccessViewModel Create() =>
		new(_dataDir, _http, _service, command =>
		{
			_elevated.Add(command);
			return Task.FromResult(_elevatedExitCode);
		}, "HOMEPC");

	private async Task<RemoteAccessViewModel> DetailsAsync()
	{
		var vm = Create();
		vm.ApiToken = "api-token";
		await vm.ContinueCommand.ExecuteAsync(null);
		Assert.True(vm.IsDetailsStage);
		return vm;
	}

	private void WriteSettings(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadSettings() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;
}
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build -c Release`
Expected: FAIL — errors naming `RemoteAccessViewModel` (`CS0234` for the `using static`, `CS0246` for the uses): the type does not exist yet.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Tray/ViewModels/RemoteAccessViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using AiChromeProxy.Infrastructure.Cloudflare;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>
/// "Set up remote access": one Cloudflare API token, a zone, a subdomain and the allowed emails give a tunnel, a DNS record, an Access
/// application and the Server settings. The API token lives only in this object (never written, never logged) and is cleared on success.
/// </summary>
/// <param name="http">Used only for <see cref="CloudflareApi.BaseUrl"/> (tests pass a fake handler).</param>
/// <param name="runElevated">The tray's elevated <c>--admin install</c> flow: exit code, or null when UAC was declined.</param>
/// <param name="machineName">Names the tunnel (<see cref="RemoteAccessProvisioner.TunnelName"/>).</param>
public sealed partial class RemoteAccessViewModel(
	DataDirectory dataDir,
	HttpClient http,
	IServiceControl service,
	Func<string, Task<int?>> runElevated,
	string machineName) : ObservableObject
{
	public const string CreateTokenUrl = "https://dash.cloudflare.com/profile/api-tokens";

	private CloudflareApi? _api;

	public enum WizardStage
	{
		Token,
		Details,
		Progress,
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsTokenStage), nameof(IsDetailsStage), nameof(IsProgressStage), nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial WizardStage Stage { get; private set; }

	public bool IsTokenStage => Stage == WizardStage.Token;

	public bool IsDetailsStage => Stage == WizardStage.Details;

	public bool IsProgressStage => Stage == WizardStage.Progress;

	[ObservableProperty]
	public partial string ApiToken { get; set; } = string.Empty;

	[ObservableProperty]
	public partial IReadOnlyList<CloudflareZone> Zones { get; private set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PublicUrl))]
	public partial CloudflareZone? SelectedZone { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PublicUrl))]
	public partial string Subdomain { get; set; } = "code";

	/// <summary>Allowed email addresses, comma or newline separated.</summary>
	[ObservableProperty]
	public partial string Emails { get; set; } = string.Empty;

	/// <summary>The address the setup produces, shown live while typing; empty until a zone is chosen.</summary>
	public string PublicUrl => SelectedZone is null ? string.Empty : $"https://{NormalizedSubdomain}.{SelectedZone.Name}/";

	[ObservableProperty]
	public partial IReadOnlyList<string> Errors { get; private set; } = [];

	/// <summary>One line per finished provisioning step.</summary>
	public ObservableCollection<string> Steps { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(SetUpCommand), nameof(BackCommand))]
	public partial bool IsBusy { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanGoBack))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial bool Failed { get; private set; }

	[ObservableProperty]
	public partial bool Succeeded { get; private set; }

	[ObservableProperty]
	public partial string? Status { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
	public partial bool IsInstallOffered { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
	public partial bool IsRestartOffered { get; private set; }

	/// <summary>Details → Token, or back to Details after a failed setup.</summary>
	public bool CanGoBack => !IsBusy && (Stage == WizardStage.Details || (Stage == WizardStage.Progress && Failed));

	private string NormalizedSubdomain => Subdomain.Trim().ToLowerInvariant();

	/// <summary>First run: the settings file has no <c>Server:PublicHost</c> yet (an unreadable file counts as set up: the wizard could not write it either).</summary>
	public static bool NeedsSetup(DataDirectory dataDir)
	{
		try
		{
			return string.IsNullOrWhiteSpace((string?)SettingsFile.LoadOrEmpty(dataDir)[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)]);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>Token → Details: verifies the token and loads the zones it can see.</summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task ContinueAsync()
	{
		Errors = [];
		if (string.IsNullOrWhiteSpace(ApiToken))
		{
			Errors = ["Paste a Cloudflare API token."];
			return;
		}

		IsBusy = true;
		try
		{
			var api = new CloudflareApi(http, ApiToken.Trim());
			await api.VerifyTokenAsync(CancellationToken.None);
			var zones = await api.ListZonesAsync(CancellationToken.None);
			if (zones.Count == 0)
			{
				Errors = ["The token can see no active zone. Give it Zone: Zone Read and Zone: DNS Edit for your domain."];
				return;
			}

			_api = api;
			Zones = zones;
			SelectedZone = zones[0];
			Stage = WizardStage.Details;
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>Details → Progress: validates, provisions, then writes the four Server settings.</summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task SetUpAsync()
	{
		var subdomain = NormalizedSubdomain;
		var emails = RemoteAccessInput.ParseEmails(Emails);
		Errors = RemoteAccessInput.Validate(subdomain, SelectedZone?.Name, emails);
		if (Errors.Count > 0 || _api is null || SelectedZone is null)
		{
			return;
		}

		Steps.Clear();
		Failed = false;
		Status = null;
		Stage = WizardStage.Progress;
		IsBusy = true;
		try
		{
			var request = new RemoteAccessRequest(SelectedZone, subdomain, emails, ReadPort(), machineName);
			var result = await new RemoteAccessProvisioner(_api).ProvisionAsync(request, new StepProgress(Steps), CancellationToken.None);
			SettingsFile.Update(dataDir, settings =>
			{
				var access = SettingsFile.Section(settings, CloudflareAccessOptions.Section);
				access[nameof(CloudflareAccessOptions.TeamDomain)] = result.TeamDomain;
				access[nameof(CloudflareAccessOptions.Audience)] = result.Audience;
				SettingsFile.Section(settings, ServerOptions.Section)[nameof(ServerOptions.PublicHost)] = result.PublicHost;
				SettingsFile.Section(settings, TunnelOptions.Section)[nameof(TunnelOptions.Token)] = result.TunnelToken;
			});
			Steps.Add($"Settings saved to {dataDir.SettingsFile}");

			ApiToken = string.Empty;
			_api = null;
			Succeeded = true;
			OfferService();
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
			Failed = true;
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>Progress (after a failure) → Details; Details → Token.</summary>
	[RelayCommand(CanExecute = nameof(CanGoBack))]
	private void Back()
	{
		Errors = [];
		Stage = Stage == WizardStage.Progress ? WizardStage.Details : WizardStage.Token;
	}

	[RelayCommand(CanExecute = nameof(IsInstallOffered))]
	private async Task InstallServiceAsync()
	{
		Errors = [];
		try
		{
			var exitCode = await runElevated(AdminCommand.Install);
			if (exitCode is not (null or 0 or AdminCommand.Cancelled))
			{
				throw new InvalidOperationException($"Service install did not complete (exit code {exitCode}).");
			}
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}

		OfferService();
	}

	[RelayCommand(CanExecute = nameof(IsRestartOffered))]
	private async Task RestartServiceAsync()
	{
		Errors = [];
		try
		{
			await service.StopAsync(CancellationToken.None);
			await service.StartAsync(CancellationToken.None);
			IsRestartOffered = false;
			Status = "Service restarted: remote access is live.";
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
	}

	private bool IsIdle() => !IsBusy;

	/// <summary>Install when there is no service yet, restart a running one (it reads settings at start), else they apply at the next start.</summary>
	private void OfferService()
	{
		var state = service.GetState();
		IsInstallOffered = state == ServiceState.NotInstalled;
		IsRestartOffered = state is ServiceState.Running or ServiceState.Starting;
		Status = state switch
		{
			ServiceState.NotInstalled => "Remote access is set up. Install the service to start it.",
			ServiceState.Running or ServiceState.Starting => "Remote access is set up. Restart the service to apply.",
			_ => "Remote access is set up. It applies when the service starts.",
		};
	}

	private int ReadPort() =>
		int.TryParse(
			SettingsFile.LoadOrEmpty(dataDir)[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString(),
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out var port) && port is >= 1 and <= 65535
			? port
			: ServerOptions.DefaultPort;

	/// <summary>Adds lines synchronously (the BCL <c>Progress</c> would post them to the thread pool).</summary>
	private sealed class StepProgress(ICollection<string> steps) : IProgress<string>
	{
		public void Report(string value) => steps.Add(value);
	}
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Tray.RemoteAccessViewModelTests"` → `total: 16`, `failed: 0`.
Run the gate → `total: 331`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Tray/ViewModels/RemoteAccessViewModel.cs tests/AiChromeProxy.Tests/Tray/RemoteAccessViewModelTests.cs
git status --short
git commit -m "feat(tray): remote access wizard view model"
```

---

### Task 8: Wizard window, tray menu item, first-run auto-open

**Files:**
- Create: `src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml`, `src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml.cs`
- Modify: `src/AiChromeProxy.Tray/App.axaml.cs` (whole file)
- Modify: `tests/AiChromeProxy.Tests/Tray/WindowsSmokeTests.cs` (whole file)

**Interfaces:**
- Consumes: `RemoteAccessViewModel` and its members (Task 7); `FakeCloudflareHandler` (Task 3, tests).
- Produces: `public partial class RemoteAccessWindow : Window` (parameterless ctor, `DataContext` = `RemoteAccessViewModel`); `internal static void App.Open(Uri address)` (was `private`); tray menu item **Set up remote access…** directly above **Settings…**; the wizard opens at tray start when `RemoteAccessViewModel.NeedsSetup(dataDir)`.

Safety: the smoke test renders the window headless only; it never clicks **Create token…** or **Open** (they start the browser).

- [ ] **Step 1: Extend the headless smoke test (fails first)**

Replace the whole content of `tests/AiChromeProxy.Tests/Tray/WindowsSmokeTests.cs` with:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tests.Infrastructure;
using AiChromeProxy.Tray;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Avalonia headless (no desktop window): each window's XAML loads and binds to its view model.</summary>
public sealed class WindowsSmokeTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	/// <summary>Entry point for <see cref="HeadlessUnitTestSession.StartNew(Type)"/>.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

	[Fact]
	public async Task Windows_LoadXaml_AndBind()
	{
		Directory.CreateDirectory(_dataDir.Logs);
		await File.WriteAllTextAsync(
			Path.Combine(_dataDir.Logs, "server-20261002.clef"),
			"""{"@t":"2026-10-02T10:00:00Z","@m":"Now listening"}""" + "\n",
			TestContext.Current.CancellationToken);

		var session = HeadlessUnitTestSession.StartNew(typeof(WindowsSmokeTests));
		try
		{
			await session.Dispatch(
				() =>
				{
					var settings = new SettingsWindow { DataContext = new SettingsViewModel(_dataDir, new FakeAutoStart(), new FakeServiceControl()) };
					settings.Show();
					Assert.Contains(TextBoxes(settings), t => t.Text == "5180");
					settings.Close();

					var logsVm = new LogsViewModel(_dataDir);
					var logs = new LogsWindow { DataContext = logsVm };
					logs.Show();
					Assert.Same(logsVm.Entries, logs.FindControl<ListBox>("EntryList")!.ItemsSource);
					Assert.Single(logsVm.Entries);
					logs.Close();

					var installVm = new InstallViewModel(new FakeServiceControl(ServiceState.NotInstalled), @"HOME\jane") { Password = "secret" };
					var install = new InstallWindow(installVm);
					var closed = false;
					install.Closed += (_, _) => closed = true;
					install.Show();
					Assert.Contains(TextBoxes(install), t => t.Text == @"HOME\jane");
					Assert.Contains(TextBoxes(install), t => t.PasswordChar == '●' && t.Text == "secret");
					installVm.InstallCommand.Execute(null);
					Assert.True(SpinUntil(() => closed), "install window closes once installed");

					using var http = new HttpClient(new FakeCloudflareHandler()
						.On("GET", "user/tokens/verify", """{"status":"active"}""")
						.On("GET", "zones?status=active&page=1&per_page=50", """[{"id":"z1","name":"example.com","account":{"id":"a1","name":"Jane"}}]""", totalPages: 1));
					var wizardVm = new RemoteAccessViewModel(_dataDir, http, new FakeServiceControl(), _ => Task.FromResult<int?>(0), "HOMEPC") { ApiToken = "api-token" };
					var wizard = new RemoteAccessWindow { DataContext = wizardVm };
					wizard.Show();
					Assert.Contains(TextBoxes(wizard), t => t.PasswordChar == '●' && t.Text == "api-token");
					wizardVm.ContinueCommand.Execute(null);
					Assert.True(SpinUntil(() => wizardVm.IsDetailsStage), "wizard moves to the details stage");
					Assert.Equal(1, wizard.GetLogicalDescendants().OfType<ComboBox>().Single().ItemCount);
					Assert.Contains(TextBoxes(wizard), t => t.Text == "code");
					wizard.Close();
				},
				TestContext.Current.CancellationToken);
		}
		finally
		{
			// The await above resumes on the Avalonia dispatcher thread; Dispose joins that thread, so it must run elsewhere.
			await Task.Run(session.Dispose, TestContext.Current.CancellationToken);
		}
	}

	public void Dispose() => Directory.Delete(_dataDir.Root, recursive: true);

	private static List<TextBox> TextBoxes(Window window) => [.. window.GetLogicalDescendants().OfType<TextBox>()];

	private static bool SpinUntil(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition() && DateTime.UtcNow < deadline)
		{
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		}

		return condition();
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0246: The type or namespace name 'RemoteAccessWindow' could not be found`.

- [ ] **Step 3: Implement the window and wire it into the tray**

Create `src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:AiChromeProxy.Tray.ViewModels"
        xmlns:cf="using:AiChromeProxy.Infrastructure.Cloudflare"
        x:Class="AiChromeProxy.Tray.Views.RemoteAccessWindow"
        x:DataType="vm:RemoteAccessViewModel"
        Title="Set up remote access"
        Icon="/Assets/tray.ico"
        Width="560" SizeToContent="Height" CanResize="False"
        WindowStartupLocation="CenterScreen">
  <StackPanel Margin="16" Spacing="8">

    <!-- 1. Token -->
    <StackPanel Spacing="8" IsVisible="{Binding IsTokenStage}">
      <TextBlock TextWrapping="Wrap"
                 Text="Publishes this server at https://&lt;subdomain&gt;.&lt;your domain&gt; through Cloudflare Tunnel, behind Cloudflare Access (only the emails you list can sign in). Zero Trust must be enabled once on the account (free plan)." />
      <TextBlock TextWrapping="Wrap" Text="Create a Cloudflare API token (Custom token) with these permissions; it is used only in this window and never saved:" />
      <TextBlock Margin="12,0,0,0" TextWrapping="Wrap"
                 Text="Account — Cloudflare Tunnel: Edit&#10;Account — Access: Apps and Policies: Edit&#10;Account — Access: Organizations, Identity Providers, and Groups: Read&#10;Zone — DNS: Edit&#10;Zone — Zone: Read" />
      <Button Content="Create token…" Click="OnCreateToken" />
      <TextBlock Text="Cloudflare API token" />
      <TextBox Text="{Binding ApiToken}" PasswordChar="●" />
    </StackPanel>

    <!-- 2. Details -->
    <StackPanel Spacing="8" IsVisible="{Binding IsDetailsStage}">
      <TextBlock Text="Zone (your domain in Cloudflare)" />
      <ComboBox ItemsSource="{Binding Zones}" SelectedItem="{Binding SelectedZone}" HorizontalAlignment="Stretch">
        <ComboBox.ItemTemplate>
          <DataTemplate x:DataType="cf:CloudflareZone">
            <TextBlock Text="{Binding Name}" />
          </DataTemplate>
        </ComboBox.ItemTemplate>
      </ComboBox>
      <TextBlock Text="Subdomain" />
      <TextBox Text="{Binding Subdomain}" />
      <TextBlock Text="Allowed email addresses (comma or one per line)" />
      <TextBox Text="{Binding Emails}" AcceptsReturn="True" Height="80" TextWrapping="Wrap" />
      <TextBlock Text="{Binding PublicUrl}" FontWeight="SemiBold" />
    </StackPanel>

    <!-- 3. Progress -->
    <StackPanel Spacing="4" IsVisible="{Binding IsProgressStage}">
      <ItemsControl ItemsSource="{Binding Steps}">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="x:String">
            <TextBlock Text="{Binding}" TextWrapping="Wrap" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
      <TextBlock Text="Working…" IsVisible="{Binding IsBusy}" />
      <TextBlock Text="{Binding Status}" TextWrapping="Wrap" FontWeight="SemiBold" />
    </StackPanel>

    <ItemsControl ItemsSource="{Binding Errors}">
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="x:String">
          <TextBlock Text="{Binding}" Foreground="#C42B1C" TextWrapping="Wrap" />
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>

    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
      <Button Content="Back" Command="{Binding BackCommand}" IsVisible="{Binding CanGoBack}" />
      <Button Content="Continue" Command="{Binding ContinueCommand}" IsVisible="{Binding IsTokenStage}" IsDefault="True" />
      <Button Content="Set up" Command="{Binding SetUpCommand}" IsVisible="{Binding IsDetailsStage}" />
      <Button Content="Install service…" Command="{Binding InstallServiceCommand}" IsVisible="{Binding IsInstallOffered}" />
      <Button Content="Restart service" Command="{Binding RestartServiceCommand}" IsVisible="{Binding IsRestartOffered}" />
      <Button Content="Open" Click="OnOpen" IsVisible="{Binding Succeeded}" />
    </StackPanel>
  </StackPanel>
</Window>
```

Create `src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml.cs`:

```csharp
using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiChromeProxy.Tray.Views;

public partial class RemoteAccessWindow : Window
{
	public RemoteAccessWindow() => InitializeComponent();

	private void OnCreateToken(object? sender, RoutedEventArgs e) => App.Open(new Uri(RemoteAccessViewModel.CreateTokenUrl));

	private void OnOpen(object? sender, RoutedEventArgs e)
	{
		if (DataContext is RemoteAccessViewModel { PublicUrl.Length: > 0 } vm)
		{
			App.Open(new Uri(vm.PublicUrl));
		}
	}
}
```

Replace the whole content of `src/AiChromeProxy.Tray/App.axaml.cs` with (new: the `CloudflareHttp` field, `Open` made `internal` and moved above the private members, `ShowRemoteAccess`, the menu item, the first-run check):

```csharp
using System.Diagnostics;
using System.Reflection;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	/// <summary>The remote access wizard's connection to api.cloudflare.com (one per tray, shared by its windows).</summary>
	private static readonly HttpClient CloudflareHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			if (AdminCommand.Parse(desktop.Args) is { Command: AdminCommand.Install } admin)
			{
				ShowInstall(desktop, admin.User);
			}
			else
			{
				StartTray(desktop);
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	/// <summary>Opens <paramref name="address"/> in the default browser.</summary>
	internal static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	/// <summary>The elevated <c>--admin install</c> instance: only the password dialog; exit code 0 once installed.</summary>
	private static void ShowInstall(IClassicDesktopStyleApplicationLifetime desktop, string controlUser)
	{
		var vm = new InstallViewModel(new WindowsServiceControl(), controlUser);
		var window = new InstallWindow(vm);
		window.Closed += (_, _) => desktop.Shutdown(vm.ExitCode);
		window.Show();
	}

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var repository = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
		var updates = new UpdateOrchestrator(new VelopackUpdateSource(repository), service, UpdateOrchestrator.DefaultPendingMarker);
		var vm = new TrayViewModel(service, AdminCommand.RunElevatedAsync, updates);
		var update = new NativeMenuItem { Command = vm.UpdateCommand };
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };

		void ShowRemoteAccess() => ShowSingle(desktop, () => new RemoteAccessWindow
		{
			DataContext = new RemoteAccessViewModel(dataDir, CloudflareHttp, service, AdminCommand.RunElevatedAsync, Environment.MachineName),
		});

		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Install service…") { Command = vm.InstallCommand },
			new NativeMenuItem("Uninstall service") { Command = vm.UninstallCommand },
			new NativeMenuItemSeparator(),
			Item("Set up remote access…", ShowRemoteAccess),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Logs…", () => ShowSingle(desktop, () => new LogsWindow { DataContext = new LogsViewModel(dataDir) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			update,
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
			update.Header = vm.UpdateText;
			update.IsVisible = vm.IsUpdateAvailable;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);
		_ = vm.RunUpdateChecksAsync(TimeProvider.System, CancellationToken.None);

		// First run: nothing is published yet, so lead with the wizard.
		if (RemoteAccessViewModel.NeedsSetup(dataDir))
		{
			ShowRemoteAccess();
		}

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
```

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)` (the XAML is compiled with compiled bindings, so a wrong property name fails here).
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Tray.WindowsSmokeTests"` → `total: 1`, `failed: 0`.
Run the gate → `total: 331`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml src/AiChromeProxy.Tray/Views/RemoteAccessWindow.axaml.cs src/AiChromeProxy.Tray/App.axaml.cs tests/AiChromeProxy.Tests/Tray/WindowsSmokeTests.cs
git status --short
git commit -m "feat(tray): Set up remote access window, menu item and first-run auto-open"
```

---

### Task 9: Release bundles a pinned, verified `cloudflared.exe`

**Files:**
- Modify: `.github/workflows/release.yml`
- Create: `THIRD-PARTY-NOTICES.md`

**Interfaces:**
- Consumes: `CloudflaredSupervisor.BundledFileName` = `cloudflared.exe`, resolved next to the Server (`AppContext.BaseDirectory`), i.e. `publish/server/cloudflared.exe` in the package.
- Produces: job-level env `CLOUDFLARED_VERSION`, `CLOUDFLARED_SHA256`; package files `server\cloudflared.exe` and `THIRD-PARTY-NOTICES.md` (root).

- [ ] **Step 1: Pin the version and hash**

In `.github/workflows/release.yml`, under `jobs.release.env`, directly after `VPK_VERSION: 1.2.161`, add:

```yaml
      # cloudflared bundled as server\cloudflared.exe. To update: bump both, from the release's published checksum.
      CLOUDFLARED_VERSION: 2026.9.3
      CLOUDFLARED_SHA256: f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2
```

- [ ] **Step 2: Download and verify after publishing the Server**

In the same file, insert this step between `Publish Server into server\ (self-contained, win-x64)` and `Install vpk` (GitHub runs `pwsh` steps with `$ErrorActionPreference = 'stop'`, so a failed download throws; the mismatch `throw` fails the job):

```yaml
      # Pinned and hash-checked: this binary ships inside Setup.exe and runs under the user's account.
      - name: Download cloudflared into server\ (pinned, SHA256-verified)
        shell: pwsh
        run: |
          $out = 'publish/server/cloudflared.exe'
          Invoke-WebRequest "https://github.com/cloudflare/cloudflared/releases/download/$env:CLOUDFLARED_VERSION/cloudflared-windows-amd64.exe" -OutFile $out
          $hash = (Get-FileHash $out -Algorithm SHA256).Hash
          if ($hash -ne $env:CLOUDFLARED_SHA256) { Remove-Item $out; throw "cloudflared $env:CLOUDFLARED_VERSION SHA256 mismatch: expected $env:CLOUDFLARED_SHA256, got $hash" }
          Copy-Item THIRD-PARTY-NOTICES.md publish/THIRD-PARTY-NOTICES.md

```

- [ ] **Step 3: Third-party notice**

Create `THIRD-PARTY-NOTICES.md`:

```markdown
# Third-party notices

The Windows release (`AiChromeProxy-win-Setup.exe`) bundles the following third-party software, unmodified.

## cloudflared

- File: `server\cloudflared.exe` (`cloudflared-windows-amd64.exe`, version 2026.9.3, SHA256 `f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2`)
- Copyright: Cloudflare, Inc.
- License: Apache License 2.0 — https://github.com/cloudflare/cloudflared/blob/master/LICENSE
- Source: https://github.com/cloudflare/cloudflared (tag `2026.9.3`)

The Server starts it as a child process to connect the Cloudflare Tunnel configured by the tray's **Set up remote access…** wizard.
```

- [ ] **Step 4: Validate the workflow file**

Run: `python -c "import yaml; d=yaml.safe_load(open('.github/workflows/release.yml', encoding='utf-8')); print([s.get('name') for s in d['jobs']['release']['steps']][-5:-1]); print(d['jobs']['release']['env']['CLOUDFLARED_VERSION'])"`
Expected:
```
['Publish Server into server\\ (self-contained, win-x64)', 'Download cloudflared into server\\ (pinned, SHA256-verified)', 'Install vpk', 'Pack (Setup.exe + update packages)']
2026.9.3
```
(if Python/PyYAML is not installed, skip; the next tag push validates it.) Do **not** run the download step locally — the release job is the only place it runs. Build and gate are unaffected; run them anyway: build → 0/0, gate → `total: 331`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/release.yml THIRD-PARTY-NOTICES.md
git status --short
git commit -m "build(release): bundle pinned, SHA256-verified cloudflared as server\cloudflared.exe"
```

---

### Task 10: Documentation

**Files:**
- Modify: `docs/windows-host.md`, `docs/setup/cloudflare.md`, `README.md`, `CLAUDE.md`

No code; every edit below is exact text.

- [ ] **Step 1: `docs/windows-host.md`**

1. Replace the line `Before exposing the Server, set up the tunnel and Access: [setup/cloudflare.md](setup/cloudflare.md).` with:

```markdown
Remote access (Cloudflare Tunnel + Cloudflare Access) is set up by the tray: [Remote access](#remote-access). The manual dashboard path stays in [setup/cloudflare.md](setup/cloudflare.md).
```

2. In the table under **Where things live**, add after the *Server (the service binary)* row:

```markdown
| `cloudflared` (bundled, pinned version) | `%LocalAppData%\AiChromeProxy\current\server\cloudflared.exe`, started by the Server |
```

3. In the blockquote that starts `> **Remove leftover environment variables before relying on the tray's Settings.**`, replace `` User-scope `CloudflareAccess__*`, `Server__*`, `Serilog__*`, `` with `` User-scope `CloudflareAccess__*`, `Server__*`, `Serilog__*`, `Tunnel__*`, ``.

4. Replace step 3 of **Install** (the line starting `3. **Settings…** — enter the Cloudflare Access team domain`) with:

```markdown
3. **Set up remote access…** opens by itself on the first start (no public host configured yet): see [Remote access](#remote-access). It writes the team domain, the application audience, the public host name and the tunnel token. **Settings…** shows the same values (except the tunnel token) for manual edits and the local port (default `5180`), checked with the rules the Server uses at startup. Optionally tick **Start the tray with Windows** there.
```

5. Insert this section between the end of **Install** (after the paragraph that ends `Fix: delete what the message names and retry.`) and `## Tray menu`:

```markdown
## Remote access

**Set up remote access…** publishes the Server as `https://<subdomain>.<your domain>` through a Cloudflare Tunnel, behind Cloudflare Access, without the Zero Trust dashboard. Requirements: a domain (zone) on Cloudflare, and Zero Trust enabled once on the account (https://one.dash.cloudflare.com, free plan — the wizard stops with that hint if it is not).

1. **Create an API token** — the **Create token…** button opens https://dash.cloudflare.com/profile/api-tokens. *Create Custom Token* with:
   - Account — **Cloudflare Tunnel: Edit**
   - Account — **Access: Apps and Policies: Edit**
   - Account — **Access: Organizations, Identity Providers, and Groups: Read**
   - Zone — **DNS: Edit**
   - Zone — **Zone: Read**

   The token stays in the wizard window's memory only: it is never written to disk or logged and is sent only to `api.cloudflare.com`. You can delete it in the dashboard after setup; re-running the wizard needs a token again.
2. **Continue** checks the token and lists your zones. Pick the zone, a subdomain (default `code`) and the email addresses allowed in (comma or one per line); the resulting address is shown as you type.
3. **Set up** creates or reuses, one line per step:
   - the Zero Trust team domain (read, not created);
   - the tunnel `ai-chrome-proxy-<computer name>` (remotely managed) and its token;
   - the tunnel route `<subdomain>.<zone>` → `http://127.0.0.1:<port>` (everything else answers 404);
   - a proxied DNS `CNAME` `<subdomain>.<zone>` → `<tunnel id>.cfargotunnel.com`;
   - the Access policy `AI Chrome Proxy — <host>` (allow the listed emails; sign-in by one-time PIN sent to the email);
   - the Access application `AI Chrome Proxy` for `<host>` (24 h session).

   Then it saves `CloudflareAccess:TeamDomain`, `CloudflareAccess:Audience`, `Server:PublicHost` and `Tunnel:Token` into `%ProgramData%\AiChromeProxy\appsettings.json` (other keys are kept). The tunnel token is a secret: the data folder's DACL (above) protects it, and **Settings…** never shows it.
4. **Install service…** (no service yet) or **Restart service** (running) applies it; **Open** opens `https://<host>/`.

Re-running the wizard converges instead of duplicating: the same tunnel, record, policy and application are found by name and updated (for example to change the allowed emails). A subdomain that already has a DNS record not pointing to this tunnel is refused: `<host> already has a DNS record; choose another subdomain or delete it` — the wizard never overwrites foreign records.

The tunnel runs **inside the service**: the Server starts the bundled `cloudflared` (`tunnel --no-autoupdate run`, token passed in its environment, never on the command line) when `Tunnel:Token` is set, restarts it if it exits (1 s, 2 s, 4 s … up to 60 s), and stops it with the service. On Windows `cloudflared` is tied to the Server by a job object, so even a crashed Server leaves no `cloudflared` behind. Its output appears in **Logs…** as `cloudflared: …` lines. No separate `cloudflared` service is needed — if you installed one with the manual guide, remove it (elevated): `cloudflared service uninstall`. `Tunnel:CloudflaredPath` in `appsettings.json` points the Server at another `cloudflared` binary; without the bundled one it falls back to `cloudflared` on `PATH` (development: `winget install Cloudflare.cloudflared`).

**Removing remote access** (the tray does not delete Cloudflare resources): in the Cloudflare dashboard delete the Access application `AI Chrome Proxy` and the policy `AI Chrome Proxy — <host>` (Zero Trust → Access), the tunnel `ai-chrome-proxy-<computer name>` (Zero Trust → Networks → Tunnels) and the `CNAME` record (your zone → DNS); then remove the `Tunnel` section from `appsettings.json` and restart the service.
```

6. In the **Tray menu** table, add before the `Settings…` row:

```markdown
| Set up remote access… | The Cloudflare Tunnel + Access wizard ([Remote access](#remote-access)). Opens by itself at tray start while no public host is configured. |
```

7. In **Cutting a release**, append this sentence to the paragraph that starts `Tag format:`:

```markdown
Before packing, the workflow downloads cloudflared `CLOUDFLARED_VERSION` into `server\` and fails unless its SHA256 equals `CLOUDFLARED_SHA256` (both pinned in the workflow's `env`; bump them together); it also copies `THIRD-PARTY-NOTICES.md` into the package.
```

Then, in the local reproduction code block, add after the `dotnet publish src/AiChromeProxy.Server …` line:

```powershell
Invoke-WebRequest https://github.com/cloudflare/cloudflared/releases/download/2026.9.3/cloudflared-windows-amd64.exe -OutFile publish/server/cloudflared.exe
(Get-FileHash publish/server/cloudflared.exe -Algorithm SHA256).Hash   # must equal the CLOUDFLARED_SHA256 in release.yml
```

8. At the end of **Manual acceptance checklist**, add:

```markdown
Remote access (on the home server, with a real zone):

19. Fresh zone (Zero Trust enabled, nothing created yet): the wizard runs all steps, **Install service…** / **Restart service**, then from another machine `https://<host>/` asks for the email PIN and the UI loads.
20. Re-run the wizard with another email → the policy now lists only that email; the dashboard shows one tunnel, one `CNAME`, one policy, one application (no duplicates).
21. A subdomain that already has a foreign DNS record (e.g. an `A` record) → refused with the "already has a DNS record" message; the record is unchanged.
22. Account without Zero Trust → the wizard stops with "Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry."
23. Reboot and do **not** log in → the tunnel is up (`https://<host>/` reachable).
24. Kill the Server process (`taskkill /F /IM AiChromeProxy.Server.exe`, elevated) → no `cloudflared.exe` is left running (`tasklist | findstr cloudflared`); the service restarts it.
25. **Logs…** shows `cloudflared: …` lines; no log line contains the tunnel token or the API token.
26. `server\cloudflared.exe` in the installed copy has the pinned SHA256 (`Get-FileHash`).
```

- [ ] **Step 2: `docs/setup/cloudflare.md`**

Insert after the first paragraph (the one ending `…answers \`401\` otherwise.`):

```markdown
**Default on the Windows host: the tray's wizard.** **Set up remote access…** does sections 1–4 below with one Cloudflare API token — tunnel, DNS, Access application and the Server settings — and the Server runs the bundled `cloudflared` itself: see [windows-host.md → Remote access](../windows-host.md#remote-access). The manual steps below stay as the alternative (dashboard, or running from source). Do not mix the two on one machine: with the manual path leave `Tunnel:Token` empty (the Server then starts no `cloudflared`); with the wizard, do not install the `cloudflared` service.
```

- [ ] **Step 3: `README.md`**

Replace the `**Status:**` line with:

```markdown
**Status:** skeleton — transport + Cloudflare Access, Windows host (service, tray, installer), remote access wizard (Cloudflare Tunnel + Access in one step). See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md) and [Windows host](docs/windows-host.md).
```

and replace `Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).` with `Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE). Bundled third-party software: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).`

- [ ] **Step 4: `CLAUDE.md`**

Replace the Infrastructure and Server bullets of **Stack** with:

```markdown
  - `src/AiChromeProxy.Infrastructure` → Application, Domain — Cloudflare Access options and token validator; hosting options (`ServerOptions`, `TunnelOptions`, `DataDirectory`); Cloudflare API client and `RemoteAccessProvisioner` (`Cloudflare/`); `AddInfrastructure(IConfiguration)`.
  - `src/AiChromeProxy.Server` — ASP.NET Core host and composition root (hosts Client, one SignalR hub routing `Envelope` by `Type`, Access middleware, `CloudflaredSupervisor` running `cloudflared` when `Tunnel:Token` is set).
```

- [ ] **Step 5: Check and commit**

Run: `rg -n "Tunnel__|Set up remote access|THIRD-PARTY" docs README.md CLAUDE.md` → hits in each edited file.
Run: `rg -n -F "C:\Users" docs README.md CLAUDE.md THIRD-PARTY-NOTICES.md` → no output; read the diff once more: every host name in it is `example.com` / `example.org` / `example.net` or a Cloudflare endpoint (no personal domains, user names or local paths).
Build → 0/0; gate → `total: 331`, `failed: 0`, exit code 0.

```bash
git add docs/windows-host.md docs/setup/cloudflare.md README.md CLAUDE.md
git status --short
git commit -m "docs: remote access wizard, bundled cloudflared, acceptance checklist"
```

---

## Final check (after Task 10)

- [ ] `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
- [ ] Gate → `total: 331`, `failed: 0`, exit code 0 (prototype: 97.8% line coverage).
- [ ] `Test-Path "$env:TEMP\aicp-tests"` → `False` after the run.
- [ ] `rg -n "TUNNEL_TOKEN|Tunnel:Token" src` — the token is only read from options and put into the child environment / settings file; `rg -n "LogInformation|LogWarning|LogError" src/AiChromeProxy.Server/Hosting/CloudflaredSupervisor.cs` shows no `options.Token`.
- [ ] Push the branch, open the PR, and before calling it done: `gh pr checks <pr-number>` → all green.
- [ ] The manual checklist items 19–26 run on the home server after the PR is merged and a release is cut (they need a real zone, UAC and a real service — never in CI or during implementation).

## Self-review (done while writing this plan)

- **Spec coverage:** §1 API client → Task 3 (envelope, errors, 429 cap, pagination, bearer, token never in messages); endpoints and the six steps → Task 5; input validation → Task 4; required permissions + "Create token" URL → Tasks 8 (window) and 10 (docs). §2 `TunnelOptions` → Task 3; supervisor (no token, binary resolution, `TUNNEL_TOKEN`, output logging, backoff 1→60 s, reset after 5 min, start failures, kill on shutdown, Job object, seam + `FakeTimeProvider`) → Task 6. §3 menu item + auto-open → Task 8; three stages, inline validation, progress, Back, Install/Restart/Open → Tasks 7–8; `SettingsFile` shared, unknown keys kept, Settings never shows the token → Task 2. §4 release + notices → Task 9. §5 docs + checklist → Task 10. §6 testing list → Tasks 2–8. Deferred 2a minors → Task 1. Architecture rules unchanged → Global Constraints (tests untouched, still green).
- **Placeholders:** none; every code step embeds the verified file.
- **Type consistency:** names used across tasks — `SettingsFile.Update/Section/LoadOrEmpty`, `CloudflareApi.SendAsync/ListAsync/VerifyTokenAsync/ListZonesAsync`, `RemoteAccessRequest(Zone, Subdomain, Emails, Port, MachineName)`, `RemoteAccessResult(TeamDomain, Audience, PublicHost, TunnelToken)`, `RemoteAccessProvisionerTests.FreshAccount`, `CloudflaredSupervisor(TunnelOptions, ILogger<>, TimeProvider, Func<ProcessStartInfo, Action<string>, ICloudflaredProcess>)`, `RemoteAccessViewModel(DataDirectory, HttpClient, IServiceControl, Func<string, Task<int?>>, string)` — compiled together in the prototype.
