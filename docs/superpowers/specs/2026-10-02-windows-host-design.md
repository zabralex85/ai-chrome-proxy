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
  - `<DataDir>` = `%ProgramData%\AiChromeProxy`; overridable by env var `AICP_DATA_DIR` (used by tests).
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
  3. Create service `AiChromeProxy` ("AI Chrome Proxy"), binary `<install dir>\current\server\AiChromeProxy.Server.exe`, start type Automatic, running as that account.
  4. Failure actions: restart after 10 s (three times), reset after 1 day.
  5. Service DACL grants the installing user `SERVICE_START | SERVICE_STOP | SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG`, so Start/Stop/Restart from the tray need no UAC afterwards.
  6. Create `<DataDir>` and `<DataDir>\logs` with full control for that account.
- Uninstall: stop (wait up to 30 s), delete the service. `<DataDir>` (config + logs) is kept.

### Dependencies
Tray → Domain, Infrastructure. Never Server or Application. Enforced by new architecture rules.

### Testability seam
`IServiceControl` (status, start, stop, install, uninstall) with the Windows implementation; used by view models and the update orchestrator so they are unit-tested with a fake. No other abstraction for future OSes until macOS/Linux arrive.

## 3. Installer, updates, releases

- **Velopack** package (`Velopack` NuGet in the tray; `vpk` CLI in CI). Main executable: the tray. The Server is published **self-contained win-x64** into `server\` inside the package — no .NET install needed on the home server. Tray also self-contained.
- Velopack installs per user (`%LocalAppData%\AiChromeProxy`); the service binary path points at the stable `current\server\` folder.
- **Updates:** the tray checks GitHub Releases (`GithubSource`) at start and every 24 h. "Update to vX" → download → stop service → apply update and restart the tray → Velopack after-update hook starts the service again. A failed download or apply leaves the current version running and shows the error.
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

## Done when

- Tag `v0.1.0` produces a GitHub Release with `Setup.exe`.
- Manual checklist passes on the home server.
- CI green; xunit gate ≥ 85%.

## Out of scope

- First-run wizard, `cloudflared`/Access automation (2b).
- macOS/Linux service management.
- Code signing.
- Docker.
