# Sub-project 2a — Windows host: service, tray, installer

Date: 2026-10-02. Parent: [architecture](2026-10-02-architecture-design.md). Builds on [skeleton](2026-10-02-skeleton-transport-design.md) and [clean architecture + test layers](2026-10-02-clean-arch-and-test-layers-design.md).

## Goal

A regular developer installs everything on a Windows home server with one `Setup.exe`. The Server then runs as a Windows Service that starts after power-on **without anyone logging in**, under the developer's own account (so `claude` sees their `~/.claude`). A tray app shows status, controls the service, edits settings, shows current and historical logs, and installs updates in one click.

Sub-project 2b (later, separate spec): first-run wizard automating `cloudflared` and Cloudflare Access setup.

## 1. Server as a Windows Service (`src/AiChromeProxy.Server`)

- `builder.Host.UseWindowsService()` (package `Microsoft.Extensions.Hosting.WindowsServices`). No-op when not running as a service — `dotnet run`, xunit and E2E are unaffected.
- **Logging:** Serilog (`Serilog.AspNetCore`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`).
  - Files: `<DataDir>\logs\server-YYYYMMDD.clef` — compact JSON (CLEF), one event per line; daily rolling; 14 files retained.
  - Console sink kept for `dotnet run`.
  - `<DataDir>` = `%ProgramData%\AiChromeProxy`; overridable by env var `AICP_DATA_DIR` (tests and local runs only; ignored by the elevated install, a relative value is made absolute).
- **Persistent config:** `<DataDir>\appsettings.json`, added as an optional JSON source **only when the process runs as a Windows Service** (`WindowsServiceHelpers.IsWindowsService()`) **or when `AICP_DATA_DIR` is set**. Never loaded otherwise, so a developer's machine config cannot override test settings (the failure mode seen with `appsettings.Local.json` in the skeleton). Environment variables keep priority over this file.
- **Follow-ups from the skeleton review (architecture spec, "Required follow-ups"):**
  - `AllowedHosts` derived from `Server:PublicHost` (e.g. `code.example.com`) plus `127.0.0.1` and `localhost`. Empty `PublicHost` outside Development → refuse to start (fail closed), same as the Access check. Existing Production-mode integration tests set `Server:PublicHost` explicitly; `docs/setup/cloudflare.md` adds `Server__PublicHost` to the configuration step.
  - JWKS keys are force-refreshed when older than 6 h (still at most one fetch per minute).
  - The once-per-minute throttle uses a monotonic clock (`TimeProvider.GetTimestamp` / `GetElapsedTime`) so wall-clock jumps can't stall refresh.
  - `CloudflareAccess:TeamDomain` must be a bare host name (no scheme, path, port or trailing slash); otherwise the Server refuses to start with a clear message. The same validation is reused by the tray's settings form.

## 2. Tray app (`src/AiChromeProxy.Tray`, Avalonia, `net10.0-windows`)

Avalonia `TrayIcon` + windows for Settings, Logs and the elevated Install dialog. MVVM with CommunityToolkit.Mvvm. Starts at login when "Start with Windows" is on (`HKCU\...\Run`).

### Tray menu
Status line (Running / Stopped / Starting / Stopping / Not installed) · Start · Stop · Restart · Install service… · Uninstall service · Settings… · Logs… · Open UI (`http://127.0.0.1:<port>/`) · Update to vX (only when available) · Exit.

### Status and logs — no IPC
- Service status from the Windows Service Control Manager (`System.ServiceProcess.ServiceController`), polled every 2 s while the menu or a window is open.
- Logs read directly from `<DataDir>\logs\*.clef` (shared read; the server keeps writing). Logs window: file list (current + history, newest first), virtualized entry list (timestamp, level, message, exception), filters by minimum level and text, "Follow" mode tailing the current file.

### Settings
Edits `<DataDir>\appsettings.json`: `CloudflareAccess:TeamDomain`, `CloudflareAccess:Audience`, `Server:Port`, `Server:PublicHost`; plus "Start with Windows". Validates with the same rules the Server uses (`CloudflareAccessOptions` validation from Infrastructure + PublicHost host-name check). Save → offers "Restart service to apply".

### Service install / uninstall (the only actions needing elevation)
- The tray relaunches itself elevated (`runas`, UAC) with `--admin install` / `--admin uninstall`. The elevated instance shows its own dialog for the account password — the password never appears on a command line.
- Install:
  1. Account defaults to the current user (`DOMAIN\user`); password verified with `LogonUser(LOGON32_LOGON_SERVICE)` for a clear error.
  2. Grant "Log on as a service" (`SeServiceLogonRight`) via `LsaAddAccountRights`.
  3. Create service `AiChromeProxy` ("AI Chrome Proxy"), binary `<install dir>\current\server\AiChromeProxy.Server.exe`, start type Automatic, running as that account. *(Superseded: the binary is `<DataDir>\server\AiChromeProxy.Server.exe`, see [Changes after the first real install](#changes-after-the-first-real-install-2026-10-03).)*
  4. Failure actions: restart after 10 s (three times), reset after 1 day.
  5. Service DACL grants the installing user `SERVICE_START | SERVICE_STOP | SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG`, so Start/Stop/Restart from the tray need no UAC afterwards.
  6. Create `<DataDir>` and `<DataDir>\logs`, each with a protected DACL written through a held handle (no ACEs inherited from `%ProgramData%`): SYSTEM and Administrators Full Control, the account Modify, all `(OI)(CI)`; nothing for Users, Authenticated Users or Everyone. Written only when the stored DACL differs; existing children are not rewritten. Refused when either folder is a link, or it or any entry directly inside it is owned by anyone but SYSTEM, Administrators, TrustedInstaller or the account. The elevated install ignores `AICP_DATA_DIR`.
- Uninstall: stop (wait up to 30 s), delete the service. `<DataDir>` (config + logs) is kept.

### Dependencies
Tray → Domain, Infrastructure. Never Server or Application. Enforced by new architecture rules.

### Testability seam
`IServiceControl` (status, start, stop, install, uninstall) with the Windows implementation; used by view models and the update orchestrator so they are unit-tested with a fake. No other abstraction for future OSes until macOS/Linux arrive.

## 3. Installer, updates, releases

- **Velopack** package (`Velopack` NuGet in the tray; `vpk` CLI in CI). Main executable: the tray. The Server is published **self-contained win-x64** into `server\` inside the package — no .NET install needed on the home server. Tray also self-contained.
- Velopack installs per user (`%LocalAppData%\AiChromeProxy`); the service binary path points at the stable `current\server\` folder. *(Superseded: the service runs from a copy in `<DataDir>\server\`, see [Changes after the first real install](#changes-after-the-first-real-install-2026-10-03).)*
- **Updates:** the tray checks GitHub Releases (`GithubSource`) at start and every 24 h. "Update to vX" → download → stop service → apply update and restart the tray → Velopack after-update hook starts the service again only when the pending-update marker says the update stopped it. A failed download, stop or apply leaves the current version running (service started again) and shows that error. Pre-release tags (`-suffix`) are published as GitHub pre-releases and never offered.
- **Uninstall:** Velopack before-uninstall hook stops and deletes the service (elevated `--admin uninstall`).
- **Release workflow** `.github/workflows/release.yml`, trigger: push of tag `v*`, `windows-latest`:
  1. setup-dotnet (global.json), restore, build, run the xunit gate;
  2. `dotnet publish` Server and Tray (Release, win-x64, self-contained) into the package layout;
  3. `vpk pack --packId AiChromeProxy --packVersion <tag without v> --mainExe AiChromeProxy.Tray.exe`;
  4. `vpk upload github` → GitHub Release with `AiChromeProxy-win-Setup.exe` and update packages.
  Permissions: `contents: write` for this workflow only.
- Not code-signed: Windows SmartScreen warns on first run. Documented; signing is out of scope.

## 4. Documentation

`docs/setup/cloudflare.md`: configuration step gains `Server__PublicHost` (and mentions that the tray's Settings form writes the same values). `docs/windows-host.md`: install with `Setup.exe`, service install from the tray, settings, logs, updates, uninstall, cutting a release (`git tag vX.Y.Z && git push origin vX.Y.Z`), and the manual acceptance checklist below. README links to it.

## 5. Testing

xunit (CI):
- Config source selection: `<DataDir>\appsettings.json` loaded only as a service or with `AICP_DATA_DIR`; env vars override it.
- `TeamDomain` / `PublicHost` validation; Server refuses to start on invalid values outside Development.
- `AllowedHosts`: request with an unknown `Host` → 400; public host and `127.0.0.1` → pass.
- JWKS: refresh after 6 h even for a known `kid`; throttle uses elapsed monotonic time (fake `TimeProvider` timestamps).
- CLEF parser and log filters (pure).
- Update orchestration with fake `IServiceControl` + fake update source: order is download → stop → apply; failure keeps the service running.
- Settings view model: validation messages, save writes the expected JSON.
- Architecture: Tray depends only on Domain and Infrastructure.

Manual acceptance checklist (`docs/windows-host.md`), run on a real Windows machine:
1. `Setup.exe` installs; tray appears.
2. Install service via tray (UAC + password) → status Running; `netstat` shows only `127.0.0.1:<port>`.
3. Reboot, do **not** log in; from another machine the UI loads through the tunnel.
4. Log in; tray shows Running; Logs window shows startup entries; filters and Follow work.
5. Stop / Start / Restart from the tray without UAC.
6. Publish a newer release → tray offers the update → after update the service is running the new version.
7. Uninstall from Windows Settings → service removed, `<DataDir>` kept.

Not automated: tray UI rendering and real service installation in CI.

## Changes adopted from the prototype

Verified on a prototype before planning; these override the sections above where they differ.

1. Install order: grant `SeServiceLogonRight` first, then verify the password with `LogonUser(LOGON32_LOGON_SERVICE)` (Windows returns 1385 until the right exists). Error 1326 → "Wrong password — a PIN does not work for services".
2. Uninstall marks the service for deletion first, then waits up to 30 s for it to stop, so a killed Velopack hook still ends with the service gone.
3. The tray polls service status every 2 s for its whole lifetime (cheap SCM query; tray-menu open events are not reliable on Win32).
4. "Open UI" opens `https://<PublicHost>/` when set (loopback answers 401 without an Access token); loopback only without a public host.
5. File logs and the persistent settings file are used only when a data directory is in use (service or `AICP_DATA_DIR`); `dotnet run`, xunit and E2E never write to `%ProgramData%`.
6. CLEF is written rendered (`@m`, `RenderedCompactJsonFormatter`), so the tray needs no template renderer.
7. "Install service…" is always enabled; on an existing service it reconfigures account, password and path (needed after a Windows password change).
8. `--admin install|uninstall` passes the tray's user (`DOMAIN\user`) so over-the-shoulder UAC (a different admin) still configures the right account.
9. Additions: single-instance tray (mutex); a pending-update marker so the next tray start restarts a service an interrupted update left stopped; Velopack auto-apply-on-startup disabled (it would swap files under a running service); the before-uninstall hook also removes the autostart entry; Avalonia headless smoke test of the three windows.
10. Shared types live in Infrastructure (`ServerOptions`, `HostName`, `DataDirectory`) because the tray must not reference Server.
11. Tray port validation 1–65535 (the Server also accepts 0, used by E2E for a random port).
12. The update repository URL is injected at release build time (`-p:UpdateRepository=…`); local builds have none and skip update checks — no hard-coded owner in the repo.
13. Development without `PublicHost`: AllowedHosts = `127.0.0.1;localhost` (never `*`).
14. The tests project targets `net10.0-windows` (it references the tray); Avalonia 12.1.3 (stable).
15. Never name a source folder `Logs`, `Log`, `Release`, `Debug`, `bin` or `obj` — the Visual Studio `.gitignore` silently drops them.

## Changes after the first real install (2026-10-03)

**Problem.** `Setup.exe` run over an existing install fails with "Failed to remove existing application directory". Velopack's Setup kills the processes it can see in the app folder (the tray), then renames the folder; the service process (started by the SCM, higher integrity) is not killed, keeps `current\server\*.exe` and `cloudflared.exe` open, and the rename is denied. Velopack has no hook that runs before that rename.

**Decision: the service runs from a copy outside the app folder.**

- Service binary: `<DataDir>\server\AiChromeProxy.Server.exe` (`%ProgramData%\AiChromeProxy\server`, inside the protected data directory; `<DataDir>` is always the default here — `AICP_DATA_DIR` is ignored, as in the elevated install). The app folder `current\server` stays the package layout and the source of the copy.
- Copy ("sync"): `current\server` → `<DataDir>\server.new` (complete copy), then swap: `server` → `server.old`, `server.new` → `server`, delete `server.old`. The service must be stopped first (its files are in use). A failed copy leaves the old `server` in place.
- **No elevated file work.** The elevated instance never copies, renames or deletes files in a folder the user can write; all sync and cleanup runs as the tray user (the data directory grants the account Modify).
- **Install service…**: the tray, as the user, prepares the data directory as the settings save does (no links, trusted owners, protected DACL), stops the service if it runs (the user has SERVICE_STOP; also for an old-layout service) and syncs. Only then `--admin install`, which does SCM/LSA/DACL work only, refuses unless `<DataDir>\server\AiChromeProxy.Server.exe` exists and neither it nor `server` is a reparse point, creates or reconfigures the service with the new binary path and starts it if newly created. Afterwards the tray starts a service it stopped (whatever the elevated step returned). A failed sync is not elevated.
- Velopack **install** hook (runs in the new version after Setup extracted it, not elevated): if the service exists and points to `<DataDir>\server`: remember whether it was running, stop it (the user has SERVICE_STOP), sync, start it if it was running. Nothing if the service is not installed.
- Velopack **after-update** hook: same sync; start rule unchanged (start only when the pending-update marker exists, i.e. the update stopped it). `UpdateOrchestrator` keeps stopping the service before apply (harmless; keeps the marker semantics).
- **Uninstall** (tray menu and before-uninstall hook): the elevated step only deletes the service and waits for it to stop; after it returned 0 the non-elevated caller deletes `<DataDir>\server` and any `server.new`/`server.old` (`<DataDir>` config and logs are kept).
- **Migration:** a service installed by an older version still points to `current\server`. Hooks leave it alone (old behaviour: after-update starts it from `current\server`). The tray shows "Run Install service… once to move the service out of the app folder (needed to install updates with Setup.exe)" while the service's binary path is not `<DataDir>\server\…`.
- Copy details: links inside the package are skipped, not followed; the package must contain `AiChromeProxy.Server.exe` or nothing is replaced. Renames and deletes are retried for a few seconds (the SCM reports Stopped slightly before the process lets go of its files). Leftovers are cleaned (as the user) at tray start and before every sync: a crash between the two renames is repaired (`server.old` back to `server`), stale `server.new`/`server.old` are deleted.
- **Version check:** a failed hook sync is otherwise silent, so the tray compares the product version of `<DataDir>\server\AiChromeProxy.Server.exe` (FileVersionInfo; `-p:Version` of the release build, `+` metadata ignored) with its own and shows "The service runs vX; the app is vY — run Install service… to update it" while they differ (not for an old-layout service, which gets the migration line).
- Testable decisions (paths, sync order, start rules, migration detection) live in tested code; the file copy runs on temp directories in tests; SCM calls stay behind `IServiceControl` (gains: the configured binary path).

## Done when

- Tag `v0.1.0` produces a GitHub Release with `Setup.exe`.
- Manual checklist passes on the home server.
- CI green; xunit gate ≥ 85%.

## Out of scope

- First-run wizard, `cloudflared`/Access automation (2b).
- macOS/Linux service management.
- Code signing.
- Docker.
