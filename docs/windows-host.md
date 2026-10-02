# Windows host

One `Setup.exe` installs everything on a Windows home server. The Server then runs as a Windows service that starts after power-on **without anyone logging in**, under your own account (so `claude` sees your `~/.claude`). A tray app shows the status, starts and stops the service, edits the settings, shows the logs and installs updates.

Before exposing the Server, set up the tunnel and Access: [setup/cloudflare.md](setup/cloudflare.md).

## Where things live

| What | Where |
|---|---|
| Tray app (and `Update.exe`) | `%LocalAppData%\AiChromeProxy\` (per-user Velopack install; the app itself is in `current\`) |
| Server (the service binary) | `%LocalAppData%\AiChromeProxy\current\server\AiChromeProxy.Server.exe` |
| Settings | `%ProgramData%\AiChromeProxy\appsettings.json` |
| Logs | `%ProgramData%\AiChromeProxy\logs\server-YYYYMMDD.clef` (one JSON event per line, daily, 14 files kept) |

`AICP_DATA_DIR` overrides `%ProgramData%\AiChromeProxy` (tests, local runs). The settings file is read only by the service or when `AICP_DATA_DIR` is set; environment variables still override it.

## Install

1. Download `AiChromeProxy-win-Setup.exe` from the project's GitHub Releases and run it. The installer is not code-signed, so SmartScreen warns on first run: **More info → Run anyway**. No admin rights and no .NET install are needed.
2. The tray icon appears (and a Start menu / desktop shortcut **AI Chrome Proxy**).
3. **Settings…** — enter the Cloudflare Access team domain, the application audience (AUD tag), the public host name of the tunnel and the local port (default `5180`). The form checks the values with the same rules the Server uses at startup. Optionally tick **Start the tray with Windows**.
4. **Install service…** — Windows asks for administrator approval (UAC), then a dialog asks for the account the service runs as (default: you) and its **Windows password**:
   - use the account password, not the Windows Hello PIN (for a Microsoft account, its Microsoft account password);
   - an account without a password cannot run a service — set one first;
   - the password goes to the Service Control Manager only; it is never written to a file or a command line.

   The installer grants the account *Log on as a service*, checks the password, creates the `AiChromeProxy` service (automatic start, restart after 10 s up to three times, counter reset after a day), lets you start and stop it without UAC, creates `%ProgramData%\AiChromeProxy` with full control for the account and starts the service.

If you change your Windows password later, the service can no longer log on: run **Install service…** again — on an installed service it updates the account and password.

## Tray menu

| Item | What it does |
|---|---|
| Service: running / stopped / starting… / stopping… / not installed | Status from the Service Control Manager, refreshed every 2 s. A second, greyed line shows the last error, if any. |
| Start / Stop / Restart | Controls the service; no UAC needed after install. |
| Install service… / Uninstall service | The only actions that need administrator approval. |
| Settings… | Edits `appsettings.json`. After **Save**, **Restart service** applies the change. |
| Logs… | The log viewer (below). |
| Open UI | Opens `https://<public host>/` (through the tunnel and Access); without a public host, `http://127.0.0.1:<port>/`. Local requests carry no Access token, so with Access on the loopback address answers `401`. |
| Update to vX | Shown only when a newer release exists (below). |
| Exit | Closes the tray; the service keeps running. |

## Logs

**Logs…** lists the log files, newest first, and shows the selected one: time, level, message and exception. Filter by minimum level and by text (message or exception, case-insensitive). **Follow** appends new entries every second and moves to the next file at midnight. The files are read with shared access while the service writes them.

If the service does not start, the reason is the last `Fatal` entry (for example an invalid `Server:PublicHost`); the Service Control Manager also logs failures in Event Viewer → Windows Logs → System.

## Updates

The tray checks GitHub Releases at start and then every 24 hours. **Update to vX** downloads the release, stops the service (its files are being replaced), applies the update and restarts the tray; the new version starts the service again. A failed download leaves everything as it was; a failed apply starts the service again and the error shows in the tray menu. If an update is interrupted outside the tray, the next tray start resumes the service.

## Uninstall

**Windows Settings → Apps → Installed apps → AI Chrome Proxy → Uninstall.** Windows asks for administrator approval to remove the service. `%ProgramData%\AiChromeProxy` (settings and logs) is kept. To remove everything by hand (elevated PowerShell):

```powershell
sc.exe stop AiChromeProxy; sc.exe delete AiChromeProxy
Remove-Item -Recurse "$env:ProgramData\AiChromeProxy"
```

## Cutting a release

```powershell
git tag v0.1.0
git push origin v0.1.0
```

`.github/workflows/release.yml` (tag `v*`, `windows-latest`) restores, builds, runs the xunit gate, publishes the tray and the Server self-contained for `win-x64`, packs them with Velopack (`vpk pack`) and uploads `AiChromeProxy-win-Setup.exe`, the portable zip and the update packages to a GitHub Release named after the tag. Installed trays update from the releases of the repository the release was built in.

Package layout, to reproduce locally (vpk as a local tool, not global):

```powershell
dotnet tool install vpk --version 1.2.161 --tool-path .tools
dotnet publish src/AiChromeProxy.Tray -c Release -r win-x64 --self-contained -p:Version=0.1.0 -o publish
dotnet publish src/AiChromeProxy.Server -c Release -r win-x64 --self-contained -p:Version=0.1.0 -o publish/server
.tools/vpk pack --packId AiChromeProxy --packVersion 0.1.0 --runtime win-x64 --packDir publish --mainExe AiChromeProxy.Tray.exe --packTitle "AI Chrome Proxy" --icon src/AiChromeProxy.Tray/Assets/tray.ico --outputDir releases
```

`releases\` then holds `AiChromeProxy-win-Setup.exe`, `AiChromeProxy-win-Portable.zip`, `AiChromeProxy-0.1.0-full.nupkg` and the `releases.win.json` feed. Inside the package the tray is at the root and the Server in `server\`, which installs as `%LocalAppData%\AiChromeProxy\current\server\`.

## Manual acceptance checklist

Run on a real Windows machine (none of this runs in CI: it needs a desktop session, UAC and a real service):

1. `Setup.exe` installs; the tray icon appears.
2. **Install service…** (UAC + password) → status *running*; `netstat -ano | findstr :5180` shows only `127.0.0.1:5180`.
3. Reboot and do **not** log in; from another machine the UI loads through the tunnel.
4. Log in; the tray shows *running*; **Logs…** shows the startup entries; the level/text filters and **Follow** work.
5. **Stop** / **Start** / **Restart** from the tray work without a UAC prompt.
6. Publish a newer release → the tray offers **Update to vX** → after the update the service runs the new version.
7. Uninstall from Windows Settings → the service is removed, `%ProgramData%\AiChromeProxy` is kept.

Also check once:

8. **Install service…** with a wrong password → a clear "wrong password" error and no service created; then the right password works.
9. `sc.exe qfailure AiChromeProxy` shows restart / 10000 ms three times, reset period 86400.
10. **Start the tray with Windows** → the tray is there after the next login; unticked → it is not.
