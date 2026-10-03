# Windows host

One `Setup.exe` installs everything on a Windows home server. The Server then runs as a Windows service that starts after power-on **without anyone logging in**, under your own account (so `claude` sees your `~/.claude`). A tray app shows the status, starts and stops the service, edits the settings, shows the logs and installs updates.

Remote access (Cloudflare Tunnel + Cloudflare Access) is set up by the tray: [Remote access](#remote-access). The manual dashboard path stays in [setup/cloudflare.md](setup/cloudflare.md).

## Where things live

| What | Where |
|---|---|
| Tray app (and `Update.exe`) | `%LocalAppData%\AiChromeProxy\` (per-user Velopack install; the app itself is in `current\`) |
| Server package (shipped with the tray) | `%LocalAppData%\AiChromeProxy\current\server\` — the source the service's copy is made from |
| Server (the service binary) | `%ProgramData%\AiChromeProxy\server\AiChromeProxy.Server.exe` — a copy of `current\server\`, outside the app folder so `Setup.exe` can replace the app while the service runs |
| `cloudflared` (bundled, pinned version) | `%ProgramData%\AiChromeProxy\server\cloudflared.exe`, started by the Server |
| Settings | `%ProgramData%\AiChromeProxy\appsettings.json` |
| Logs | `%ProgramData%\AiChromeProxy\logs\server-YYYYMMDD.clef` (one JSON event per line, daily, 14 files kept) |

The data folder `%ProgramData%\AiChromeProxy` therefore holds `appsettings.json`, `logs\` and `server\`. `server.new\` and `server.old\` exist only while a copy is being swapped in (leftovers of an interrupted copy are removed at the next tray start or copy). The service's copy is refreshed by **Install service…**, by `Setup.exe` and by every update — always by the tray or Velopack's hooks running as you, never by an elevated process; never edit files in it. If it ends up at another version than the tray (a copy failed), the tray menu shows *The service runs vX; the app is vY — run Install service… to update it*.

`AICP_DATA_DIR` overrides `%ProgramData%\AiChromeProxy` for tests and local runs only (a relative value is made absolute); **Install service…** ignores it and always uses `%ProgramData%\AiChromeProxy`. The settings file is read only by the service or when `AICP_DATA_DIR` is set; environment variables still override it.

> **Remove leftover environment variables before relying on the tray's Settings.** If you ran the Server from source, User-scope `CloudflareAccess__*`, `Server__*`, `Serilog__*`, `Tunnel__*`, `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` variables may also reach the service and silently win over `appsettings.json`; a leftover `AICP_DATA_DIR` moves the service to an unprotected data folder. The **Settings…** window shows a warning line naming any of them it sees; delete them (System Properties → Environment Variables, or `[Environment]::SetEnvironmentVariable("<name>", $null, "User")`) and restart the service.

## Install

1. Download `AiChromeProxy-win-Setup.exe` from the project's GitHub Releases and run it. The installer is not code-signed, so SmartScreen warns on first run: **More info → Run anyway**. No admin rights and no .NET install are needed.
2. The tray icon appears (and a Start menu / desktop shortcut **AI Chrome Proxy**).
3. **Set up remote access…** opens by itself on the first start (no public host configured yet): see [Remote access](#remote-access). It writes the team domain, the application audience, the public host name and the tunnel token. **Settings…** shows the same values (except the tunnel token) for manual edits and the local port (default `5180`), checked with the rules the Server uses at startup. Optionally tick **Start the tray with Windows** there.
4. **Install service…** — Windows asks for administrator approval (UAC), then a dialog shows the account the service runs as and asks for its **Windows password**. The Account field is read-only: the service runs as the signed-in tray user, because installs and updates replace the service's files as that user (without UAC); any other account is refused. (With over-the-shoulder UAC, where an administrator approves for a standard user, that standard user is the account.)
   - use the account password, not the Windows Hello PIN: a PIN does not work for services, so a Microsoft-account or PIN-only user needs the account password (for a Microsoft account, its Microsoft account password);
   - an account without a password cannot run a service — set one first;
   - the password goes to the Service Control Manager only; it is never written to a file or a command line.

   Before asking for approval the tray, as you, makes `%ProgramData%\AiChromeProxy` safe (the same checks as the settings save, see [Remote access](#remote-access)), stops the service if it runs and copies `current\server\` to `%ProgramData%\AiChromeProxy\server\` (which inherits the folder's DACL). If that copy fails, nothing is elevated and a service that was running is started again. The elevated part touches no files in the Server folder: it only refuses when `server\AiChromeProxy.Server.exe` is missing or a link.

   The elevated installer grants the account *Log on as a service*, checks the password, creates the `AiChromeProxy` service (automatic start, restart after 10 s up to three times, counter reset after a day), lets you start and stop it without UAC, creates `%ProgramData%\AiChromeProxy` (and its `logs` folder) and gives both a protected DACL (nothing inherited from `%ProgramData%`): SYSTEM and Administrators Full Control, the account **Modify**, inherited by files created later; Users, Authenticated Users and Everyone get nothing. It points the service at `%ProgramData%\AiChromeProxy\server\AiChromeProxy.Server.exe`. After a successful install the tray starts the service, whatever its earlier state (so re-running it after a password change brings the server back); when the approval was declined or the install failed, it starts the service again only if it had stopped it for the copy. While the service is stopped for the copy, a pending-update marker (`%TEMP%\AiChromeProxy.update-pending`) exists: if the tray is killed meanwhile, its next start starts the service.

   A data folder the tray cannot use as you (owned by Administrators without a protected DACL, or protected for another account) is refused with "Ask an administrator to delete it (back up appsettings.json and the logs folder first: they are deleted with it), then retry."

If you change your Windows password later, the service can no longer log on: run **Install service…** again — on an installed service it updates the account and password. A reinstall writes the DACL of the folder and of `logs` only when it differs from the one above; existing files inside are never rewritten.

Install refuses a data folder, or its `logs` subfolder, that is a link/junction, and refuses when the folder, `logs` or any file or folder directly inside them (for example `appsettings.json` or `appsettings.json.tmp`) is owned by anyone other than SYSTEM, Administrators, TrustedInstaller or your account (`%ProgramData%` lets standard users pre-create folders and files). Fix: delete what the message names and retry.

### Reinstall with Setup.exe

Running `Setup.exe` over an existing install (the same or a newer version) works while the service runs: Setup closes the tray and replaces the app folder, which the service no longer uses; the new version then stops the service (no UAC: you may stop it), copies its Server into `%ProgramData%\AiChromeProxy\server\` and starts it again if it was running. Nothing happens to the service when none is installed. If the copy fails, the previous copy stays and is started again.

**Services installed by an older version** still run from `%LocalAppData%\AiChromeProxy\current\server\`, which makes `Setup.exe` over them fail with "Failed to remove existing application directory". While that is the case the tray menu shows *Run Install service… once to move the service out of the app folder (needed to install updates with Setup.exe)*. Run **Install service…** once (UAC + password): it copies the Server, points the service at the copy and the line disappears. Until then, **Stop** the service before running `Setup.exe`, then **Start** it, or run **Install service…** to move it; updates from the tray menu keep working either way.

## Remote access

**Set up remote access…** publishes the Server as `https://<subdomain>.<your domain>` through a Cloudflare Tunnel, behind Cloudflare Access, without the Zero Trust dashboard. Requirements: a domain (zone) on Cloudflare, and Zero Trust enabled once on the account (https://one.dash.cloudflare.com, free plan — the wizard stops with that hint if it is not).

1. **Create an API token** — the **Create token…** button opens https://dash.cloudflare.com/profile/api-tokens. *Create Custom Token* with:
   - Account — **Cloudflare Tunnel: Edit**
   - Account — **Access: Apps and Policies: Edit**
   - Account — **Access: Organizations, Identity Providers, and Groups: Read**
   - Zone — **DNS: Edit**
   - Zone — **Zone: Read**

   Only a **user** API token (My Profile → API Tokens) works: the wizard checks it with `user/tokens/verify`, which does not accept account-owned tokens. Under *Zone Resources* include only the one zone you publish on, and set a *TTL* (expiry) of a day or so. The token is used only by the wizard window: it is never saved, never logged and is sent only to `api.cloudflare.com`. It is forgotten when setup succeeds or the window closes (closing also cancels a setup in progress; nothing is saved then). Delete it in the dashboard after setup and create a new one for a re-run.
2. **Continue** checks the token and lists your zones. Pick the zone, a subdomain (default `code`) and the email addresses allowed in (comma or one per line); the resulting address is shown as you type.
3. **Set up** creates or reuses, one line per step:
   - the Zero Trust team domain (read, not created);
   - the tunnel `ai-chrome-proxy-<computer name>` (remotely managed) and its token;
   - the tunnel route `<subdomain>.<zone>` → `http://127.0.0.1:<port>` (everything else answers 404);
   - a proxied DNS `CNAME` `<subdomain>.<zone>` → `<tunnel id>.cfargotunnel.com`;
   - the Access policy `AI Chrome Proxy — <host>` (allow the listed emails; sign-in by one-time PIN sent to the email);
   - the Access application `AI Chrome Proxy` for `<host>` (24 h session).

   Then it saves `CloudflareAccess:TeamDomain`, `CloudflareAccess:Audience`, `Server:PublicHost` and `Tunnel:Token` into `%ProgramData%\AiChromeProxy\appsettings.json` (other keys are kept). The tunnel token is a secret, protected from the first save — also when the wizard runs before **Install service…**: the tray creates the data folder with the protected DACL described under [Install](#install) (or writes it on a folder you own), and creates the file itself with its own protected DACL (SYSTEM and Administrators Full Control, your account Modify), so no other local user can read it, even when an older `appsettings.json` was readable. A data folder that is a link, contains anything owned by another user, or is owned by Administrators without a protected DACL is refused: run **Install service…** (it re-protects the folder), or delete the folder, and retry. **Settings…** saves the same way and never shows the token.
4. **Install service…** (no service yet; the service it starts already uses the new settings) or **Restart service** (running) applies it; **Open** opens `https://<host>/`.

The DNS name is checked before anything is created. A name that already has a record other than a `CNAME` to this tunnel — including a `CNAME` left from a deleted tunnel — is refused with `<host> already has a DNS record; choose another subdomain or delete it.`; the account is left unchanged and the wizard never overwrites a foreign record. Delete that record in the dashboard (your zone → DNS) or choose another subdomain.

Re-running the wizard converges instead of duplicating: the same tunnel, record, policy and application are found by name (the application by its domain) and updated, for example to change the allowed emails. If an Access application for the same domain already exists (for example one created by hand), the wizard updates it to its own settings: name, policy and 24 h session. A re-run with a **different** subdomain creates a new record, policy and application and leaves the old ones in place (the old host then answers 404 from the tunnel): delete them as in **Removing remote access** below.

The tunnel runs **inside the service**: the Server starts the bundled `cloudflared` (`tunnel --no-autoupdate run`, token passed in its environment, never on the command line) when `Tunnel:Token` is set, restarts it if it exits (1 s, 2 s, 4 s … up to 60 s), and stops it with the service. The Server does not start the tunnel while the Cloudflare Access check is disabled (Development): a Server without Access must not be reachable from the internet. On Windows `cloudflared` is tied to the Server by a job object, so even a crashed Server leaves no `cloudflared` behind. Its output appears in **Logs…** as `cloudflared: …` lines. No separate `cloudflared` service is needed — if you installed one with the manual guide, remove it (elevated): `cloudflared service uninstall`. `Tunnel:CloudflaredPath` in `appsettings.json` points the Server at another `cloudflared` binary; without the bundled one it falls back to `cloudflared` on `PATH` (development: `winget install Cloudflare.cloudflared`).

**Browser Cache TTL.** Cloudflare's zone default (*Caching → Configuration → Browser Cache TTL*, 4 hours) can rewrite the Server's `Cache-Control: no-cache` into `max-age=14400`, which used to keep browsers on the old client for hours after an update. The app no longer depends on it: the page is sent `no-store` and names only fingerprinted (immutable) files, and an open tab offers **Reload** when the Server's version changes. Still, set Browser Cache TTL to **Respect Existing Headers** so the zone does not override the Server's caching. A tab that still runs an old client is fixed by a hard reload (**Ctrl+Shift+R**).

**Removing remote access** (the tray does not delete Cloudflare resources): in the Cloudflare dashboard delete the Access application `AI Chrome Proxy` and the policy `AI Chrome Proxy — <host>` (Zero Trust → Access), the tunnel `ai-chrome-proxy-<computer name>` (Zero Trust → Networks → Tunnels) and the `CNAME` record (your zone → DNS); then remove the `Tunnel` section from `appsettings.json` and restart the service.

## Tray menu

| Item | What it does |
|---|---|
| Service: running / stopped / starting… / stopping… / not installed | Status from the Service Control Manager, refreshed every 2 s. A second, greyed line shows the last error, else the request to run **Install service…** once (service still in the app folder), else a version mismatch between the service's copy and the tray. |
| Start / Stop / Restart | Controls the service; no UAC needed after install. |
| Install service… / Uninstall service | The only actions that need administrator approval. |
| Set up remote access… | The Cloudflare Tunnel + Access wizard ([Remote access](#remote-access)). Opens by itself at tray start while no public host is configured (neither in `appsettings.json` nor as a `Server__PublicHost` environment variable). |
| Settings… | Edits `appsettings.json` (saved atomically: temp file, then replace; protected as in [Remote access](#remote-access)). After **Save**, **Restart service** applies the change. |
| Logs… | The log viewer (below). |
| Open UI | Opens `https://<public host>/` (through the tunnel and Access); without a public host, `http://127.0.0.1:<port>/`. Local requests carry no Access token, so with Access on the loopback address answers `401`. |
| Update to vX | Shown only when a newer release exists (below). |
| Exit | Closes the tray; the service keeps running. |

## Logs

**Logs…** lists the log files, newest first, and shows the selected one: time, level, message and exception. Filter by minimum level and by text (message or exception, case-insensitive). **Follow** appends new entries every second and moves to the next file at midnight. Picking an older file turns **Follow** off; turning it on again jumps to the newest file. A large file is read from its last 16 MB, and at most 50,000 entries are kept (the oldest are dropped). The files are read with shared access while the service writes them.

If the service does not start, the reason is the last `Fatal` entry (for example an invalid `Server:PublicHost`); the Service Control Manager also logs failures in Event Viewer → Windows Logs → System.

## Updates

The tray checks GitHub Releases at start and then every 24 hours. **Update to vX** downloads the release, stops the service, applies the update and restarts the tray; the new version copies its Server into `%ProgramData%\AiChromeProxy\server\` (a service still running from the app folder, installed by an older version, is not copied but runs the new files there) and starts the service again only if the update stopped it (a service you stopped stays stopped, with the new files). If the copy fails, the previous copy stays and the service starts from it; the next update or **Install service…** copies again. A failed download leaves everything as it was; a failed stop or apply starts the service again and that error shows in the tray menu. If an update is interrupted outside the tray, the next tray start resumes the service. Pre-releases (tags with a `-suffix`) are never offered.

## Uninstall

**Windows Settings → Apps → Installed apps → AI Chrome Proxy → Uninstall.** Windows asks for administrator approval to remove the service (the prompt may appear behind other windows); once that succeeded, the uninstaller deletes the service's copy of the Server, `%ProgramData%\AiChromeProxy\server`, as you; the **Start the tray with Windows** entry is removed too. `%ProgramData%\AiChromeProxy` (settings and logs) is kept. If the prompt is not approved within about 25 seconds, or the uninstall hook fails, the app is removed but the service stays: delete it with `sc.exe delete AiChromeProxy` (elevated).

**Uninstall service** in the tray does the same (the elevated step removes the service, then the tray deletes `server\`). If the elevated step fails, it shows "Service uninstall did not complete (exit code N)", where N is a Win32 error code (for example 5 = access denied); if only `server\` could not be deleted, it shows "The service was removed, but … could not be deleted …; delete it by hand." No error file is written. Remove the service by hand (elevated PowerShell), and the data too if you want everything gone:

```powershell
sc.exe stop AiChromeProxy; sc.exe delete AiChromeProxy
Remove-Item -Recurse "$env:ProgramData\AiChromeProxy\server"   # the service's copy of the Server
Remove-Item -Recurse "$env:ProgramData\AiChromeProxy"          # settings and logs too
```

## Cutting a release

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Tag format: `vMAJOR.MINOR.PATCH`, optionally with a `-suffix` (for example `v0.1.0-rc1`). `.github/workflows/release.yml` (tag `v*`, `windows-latest`) restores, builds, runs the xunit gate, publishes the tray and the Server self-contained for `win-x64`, packs them with Velopack (`vpk pack`) and uploads `AiChromeProxy-win-Setup.exe`, the portable zip and the update packages to a published (not draft) GitHub Release titled `AI Chrome Proxy vX` (the tag). A tag that does not match `vMAJOR.MINOR.PATCH[-suffix]` fails the workflow; a `-suffix` tag is published as a GitHub pre-release, which installed trays never offer. Installed trays update from the releases of the repository the release was built in. Before packing, the workflow downloads cloudflared `CLOUDFLARED_VERSION` into `server\` and fails unless its SHA256 equals `CLOUDFLARED_SHA256` (both pinned in the workflow's `env`; bump them together); it also copies `THIRD-PARTY-NOTICES.md` and `licenses\` (the cloudflared Apache-2.0 license text) into the package.

Package layout, to reproduce locally (vpk as a local tool, not global):

```powershell
dotnet tool install vpk --version 1.2.161 --tool-path .tools
dotnet publish src/AiChromeProxy.Tray -c Release -r win-x64 --self-contained -p:Version=0.1.0 -p:UpdateRepository=https://github.com/<owner>/<repo> -o publish
dotnet publish src/AiChromeProxy.Server -c Release -r win-x64 --self-contained -p:Version=0.1.0 -o publish/server
Invoke-WebRequest https://github.com/cloudflare/cloudflared/releases/download/2026.9.3/cloudflared-windows-amd64.exe -OutFile publish/server/cloudflared.exe
(Get-FileHash publish/server/cloudflared.exe -Algorithm SHA256).Hash   # must equal the CLOUDFLARED_SHA256 in release.yml
.\.tools\vpk.exe pack --packId AiChromeProxy --packVersion 0.1.0 --runtime win-x64 --packDir publish --mainExe AiChromeProxy.Tray.exe --packTitle "AI Chrome Proxy" --icon src/AiChromeProxy.Tray/Assets/tray.ico --outputDir releases
```

Without `-p:UpdateRepository=...` a local build never checks for updates (the release workflow sets it). `.tools/` is gitignored. `releases\` then holds `AiChromeProxy-win-Setup.exe`, `AiChromeProxy-win-Portable.zip`, `AiChromeProxy-0.1.0-full.nupkg` and the `releases.win.json` feed. Inside the package the tray is at the root and the Server in `server\`, which installs as `%LocalAppData%\AiChromeProxy\current\server\`; **Install service…**, `Setup.exe` and updates copy it to `%ProgramData%\AiChromeProxy\server\`, where the service runs it.

## Manual acceptance checklist

Run on a real Windows machine (none of this runs in CI: it needs a desktop session, UAC and a real service):

1. `Setup.exe` installs; the tray icon appears.
2. **Install service…** (UAC + password) → status *running*; `netstat -ano | findstr :5180` shows only `127.0.0.1:5180`.
3. Reboot and do **not** log in; from another machine the UI loads through the tunnel.
4. Log in; the tray shows *running*; **Logs…** shows the startup entries; the level/text filters and **Follow** work.
5. **Stop** / **Start** / **Restart** from the tray work without a UAC prompt.
6. Publish a newer release → the tray offers **Update to vX** → after the update the service runs the new version.
7. Uninstall from Windows Settings → the service and `%ProgramData%\AiChromeProxy\server` are removed, `appsettings.json` and `logs` are kept.

Also check once:

8. **Install service…** with a wrong password → the message `Wrong password for <account>. Use the Windows account password (a PIN does not work for services).` (not a bare Win32 error) and no service created; then the right password works.
9. `sc.exe qfailure AiChromeProxy` shows restart / 10000 ms three times, reset period 86400.
10. **Start the tray with Windows** → the tray is there after the next login; unticked → it is not.
11. `sc.exe qc AiChromeProxy`: `BINARY_PATH_NAME` is `"C:\ProgramData\AiChromeProxy\server\AiChromeProxy.Server.exe"` (quoted) and `SERVICE_START_NAME` is your account. `sc.exe sdshow AiChromeProxy`: an ACE for your SID with start/stop/query rights only.
12. `icacls "$env:ProgramData\AiChromeProxy"` and `icacls "$env:ProgramData\AiChromeProxy\logs"`: only `NT AUTHORITY\SYSTEM:(OI)(CI)(F)`, `BUILTIN\Administrators:(OI)(CI)(F)` and your account `(OI)(CI)(M)`, none marked `(I)`; no `Users`, `Authenticated Users` or `Everyone`. `icacls "$env:ProgramData\AiChromeProxy\server"`: the same three entries, all marked `(I)` (inherited). As another standard user, creating a file in any of the three folders fails.
13. Change your Windows password, then re-run **Install service…** → it succeeds; `icacls "$env:ProgramData\AiChromeProxy"` is the same before and after.
14. **Uninstall service** while the service is *Running*, and again while it is *Start-pending* → the service is gone (`sc.exe query AiChromeProxy` → 1060), `server\` is gone, `appsettings.json` and `logs` are kept.
15. Over-the-shoulder UAC (standard user, an administrator approves) → the service runs as the standard user and the ACE carries the standard user's SID.
16. Update with the service *running* → after the update it runs the new version. Update with the service *stopped* beforehand → after the update it is still stopped. Uninstall from Apps & features → the service and the autostart entry are removed.
17. **Update to vX** appears only on a `Setup.exe`-installed copy, never under `dotnet run`.
18. With a User-scope `Server__PublicHost` variable set, **Settings…** shows the environment-variable warning naming it; without it, no warning.

Remote access (on the home server, with a real zone):

19. Fresh zone (Zero Trust enabled, nothing created yet): the wizard runs all steps, **Install service…** / **Restart service**, then from another machine `https://<host>/` asks for the email PIN and the UI loads.
20. Re-run the wizard with another email → the policy now lists only that email; the dashboard shows one tunnel, one `CNAME`, one policy, one application (no duplicates).
21. A subdomain that already has a foreign DNS record (e.g. an `A` record, or a `CNAME` left from a deleted tunnel) → refused with the "already has a DNS record" message; the record is unchanged and no tunnel was created.
22. An Access application for the same domain created by hand → the wizard updates it (name `AI Chrome Proxy`, the wizard's policy, 24 h session) instead of creating a second one.
23. Account without Zero Trust → the wizard stops with "Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry."
24. Reboot and do **not** log in → the tunnel is up (`https://<host>/` reachable).
25. Kill the Server process (`taskkill /F /IM AiChromeProxy.Server.exe`, elevated) → no `cloudflared.exe` is left running (`tasklist | findstr cloudflared`); the service restarts it.
26. **Logs…** shows `cloudflared: …` lines.
27. After the first real tunnel run, search the Server log files for the tunnel token and the API token: `rg -F "<token>" "$env:ProgramData\AiChromeProxy\logs"` → no match.
28. `server\cloudflared.exe` in the installed copy has the pinned SHA256 (`Get-FileHash`).

Service outside the app folder (`Setup.exe` over an existing install):

29. With the service *running* (tunnel up), run `Setup.exe` of the same or a newer version → it completes without "Failed to remove existing application directory" and without UAC; afterwards the service is *running* the new version (`(Get-Item "$env:ProgramData\AiChromeProxy\server\AiChromeProxy.Server.exe").VersionInfo.ProductVersion`) and the tunnel is back.
30. The same with the service *stopped* → it is still stopped afterwards, and `server\` holds the new version.
31. After 29, 30 and an update from the tray menu: no `server.new` or `server.old` in `%ProgramData%\AiChromeProxy`.
32. Migration: with a service installed by a version before this change (`sc.exe qc` shows `...\current\server\...`) → the tray shows the *Run Install service… once…* line; an update from the tray menu still restarts it from `current\server`; **Install service…** → `sc.exe qc` shows the `%ProgramData%` path, the line disappears, the service runs; then item 29 passes.
33. `Setup.exe` with no service installed → installs normally; no `server\` folder is created.
34. **Install service…** while the service runs → it is stopped before the UAC prompt and running again afterwards; decline the UAC prompt → it is running again. **Install service…** on a *stopped* service → it is running afterwards. Kill the tray (`taskkill /F /IM AiChromeProxy.Tray.exe`) while the UAC prompt is open → `%TEMP%\AiChromeProxy.update-pending` exists; start the tray → the service runs again and the file is gone. During the elevated step `Get-Process AiChromeProxy.Tray` shows the elevated instance, and Process Monitor (filter: path contains `ProgramData\AiChromeProxy\server`) shows no writes or deletes by it.
35. Version line: replace `%ProgramData%\AiChromeProxy\server\` with an older release's Server (service stopped), start the tray → *The service runs vX; the app is vY — run Install service… to update it*; **Install service…** → the line disappears. `(Get-Item "$env:ProgramData\AiChromeProxy\server\AiChromeProxy.Server.exe").VersionInfo.ProductVersion` starts with the release version (the tag without `v`).
