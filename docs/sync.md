# Sync (browser ⇄ home server)

The folder you open in Chrome is mirrored to the home server and kept in sync. Edits made on the server (by Claude or by hand) come back to the folder (the [back channel](#back-channel)). Design: [sync and shell](superpowers/specs/2026-10-03-sync-and-shell-design.md), [back channel](superpowers/specs/2026-10-03-back-channel-design.md).

Requires **Chrome 123 or newer** (File System Access API; the theme uses CSS `light-dark()`). The browser code is strict TypeScript (`src/AiChromeProxy.Client/Scripts`) compiled by `dotnet build`; no Node.js is needed.

## The app shell

```
+------------------------------------------------------------------+
| folder · connection pill · theme toggle                   user   |
+-----------+------------------------------------+-----------------+
| file tree | [Welcome] [file.cs] [Errors] tabs  | Actions history |
|           |  main area                         |                 |
|           +------------------------------------+                 |
|           |  chat input (disabled for now)     |                 |
+-----------+------------------------------------+-----------------+
| sync      |                                    | connection      |
| status    |                                    | status          |
+-----------+------------------------------------+-----------------+
```

- **Top bar:** app name, folder name, connection pill (Connected / Connecting… / Reconnecting… / Offline; the tooltip shows the ping latency), theme toggle (system → light → dark, kept in `localStorage`), panel toggles, and the user: the Cloudflare Access email, or "local" when there is none (Development). When the server runs another version than the page (an update was installed while the tab was open; the `pong` payload carries an optional `serverVersion`), a non-blocking **A new version is installed — Reload** banner appears.
- **Left:** **Open folder** (or the folder name with **Change**); when access is lost or the server refuses the folder, a notice with **Restore access** and **Change folder**. The tree shows a sync badge per file (synced, pending, too large, error). At the bottom the **sync status**: "Synced 1 234 files · Rescan in 7 s", "Uploading 12/80", "Access needed"; the error count opens the **Errors** tab.
- **Centre:** tabs (a permanent Welcome tab, one closable tab per opened file showing its metadata, the Errors tab), the main area, and a disabled chat input (Claude chat arrives in a later sub-project).
- **Right:** **Actions history**, newest first, at most 500 entries: folder opened, pass started/finished (files, bytes, duration; "Already in sync" when nothing changed), uploads and deletes, errors with the server's message, reconnects, access lost. At the bottom the **connection status** with the ping latency (measured every 15 s).
- Both side panels resize (drag or arrow keys on the separator) and collapse; collapsed panels keep their state. The tree and tabs work with the keyboard (arrows, Home/End, Enter).

## How it works

1. **Open folder** calls `showDirectoryPicker({mode: "read"})` (`Scripts/fsaccess.ts`). The folder handle is kept in IndexedDB (best effort: without IndexedDB everything still works, only the hash cache and **Restore access** are lost). After a reload Chrome asks again with one click (**Restore access**).
2. Every 10 s while the tab is visible, and at once when it gets focus, the browser reads the root `.gitignore`, walks the folder (without entering the built-in excluded folders and the folders the `.gitignore` excludes), drops excluded files, hashes new or changed files (SHA-256 via WebCrypto, cached by size and modification time) and tells the server what changed. Only one scan runs at a time.
3. The server answers with the paths it is missing; the browser uploads them in 16 KB chunks, in order and pipelined: the next file's chunks go out without waiting for the previous file's `sync.stored`, with at most 16 files awaiting their reply (so a first sync over a slow link is not one round trip per file; the server still stores one file after the other). If the connection drops, the replies still awaited are lost and the pass ends; the next scan starts over with the full manifest. Each file is written to `<path>.<session>.aicp-tmp` (a temp name per connection, so a reconnect never collides with the old connection's unfinished upload) and moved into place only after its SHA-256 matched.

| Message (`Envelope.type`) | Direction | Payload (camelCase) |
|---|---|---|
| `sync.open` → `sync.opened` | client → server → client | `{repo}` — the folder name; the reply carries the sanitized name |
| `sync.manifest` → `sync.need` | client → server → client | `{repo, entries[{path, size, sha256}], final, keep?[]}` (pages of ≤ 500 entries and ≤ 24 000 bytes; `keep` pages follow the entry pages and count toward the same limits) → `{repo, paths[]}`. `keep` (optional) lists what the browser could not sync but the mirror must keep: file paths, and folder prefixes ending in `/`; nothing is uploaded for them |
| `sync.delta` → `sync.need` | client → server → client | `{repo, upserts[{path, size, sha256}], deletes[]}` → `{repo, paths[]}` |
| `sync.chunk` → `sync.stored` | client → server → client | `{repo, path, offset, data, last, sha256?}` → `{repo, path}` (only the last chunk is answered; when an earlier chunk of the upload failed, the rest is dropped and the last chunk gets that first error). `data` is **base64url without padding** (`-` and `_`, no `+`, `/` or `=`), at most 16 KB raw, so a chunk stays well under SignalR's message limit |
| `sync.opened` (reply to `sync.open`) | server → client | `{repo, settings?}` — the project settings |
| `sync.remote` | server → client | `{repo, changes[{path, sha256?, size, base?}]}` (≤ 500 changes and ≤ 24 000 bytes per page); `sha256` is absent for a deletion, `base` is the last agreed hash |
| `sync.fetch` → `sync.data` | client → server → client | `{repo, path, offset}` → `{repo, path, offset, data, last, sha256?}` (≤ 16 KB raw, base64url; `sha256` on the last chunk; `not_found` when the file is gone) |
| `sync.ack` | client → server → client | `{repo, path, sha256?}`; echoed back once the change is applied |
| `project.settings.get` / `project.settings.set` → `project.settings` | client → server → client | `{repo}` / `{repo, settings}` → `{repo, settings}` |
| `error` | server → client | `{code, message?}` with the request's `correlationId`; `code` is `bad_request`, `not_found`, `too_large`, `unknown_type` or `internal` |

After the last manifest page the server deletes mirror files that are neither in the manifest nor covered by `keep` (an exact path or a kept folder prefix, ignoring case; an invalid `keep` entry is refused with `bad_request`). The browser sends the full manifest on every new session (first sync, page reload, reconnect, folder change) and then every 10 minutes (cheap: the server caches hashes), deltas in between. The server refuses (`bad_request`) an empty manifest (no entries and no `keep`), and a delta that would delete every file, while the mirror has files: an empty folder over a non-empty mirror is never applied (the explorer shows the message with **Change folder** and **Restore access**). More than 20 000 files waiting for upload is refused with `too_large`.

## Back channel

The server keeps, per file, a **base**: the last SHA-256 both sides agreed on, in SQLite (WAL) at `<DataDir>\aicp.db` (dev: `data\aicp.db` under the content root; `Projects:Database` overrides). Each file is decided three ways from the browser's hash, the mirror's hash and the base: equal → in sync; the mirror still at the base → the browser's change is uploaded (or deleted); the browser still at the base → the server's change is pushed; both changed → pushed as a conflict candidate. Before a repo's first full manifest after upgrading, the browser wins as in 3a; afterwards the three-way rule applies. Bases survive a service restart.

1. A `FileSystemWatcher` on the mirror (500 ms quiet, at most 3 s) notices a server edit or delete and pushes `sync.remote`.
2. The browser fetches the file (`sync.fetch`), checks the hash, re-hashes its own file after the fetch and writes only if the file is still the agreed version; then it sends `sync.ack`. If the file changed meanwhile, it is a **conflict**.
3. A conflict opens in the **Conflict** tab (server text preview up to 256 KB): **Keep mine** uploads the browser's version, **Take server's** writes the server's.
4. Writing needs the browser's write permission. Without it the Explorer shows "N server changes — Allow writing"; one click grants it. With **Apply server changes automatically** off it shows "N server changes waiting — **Apply all**" (or **Apply** per change).
5. Excluded files (built-ins, `.gitignore`, the project's extra excludes) are never deleted, pushed or written on either side: build output on the mirror (`bin/`, `obj/`) stays there.
6. An upload whose mirror file changed meanwhile is not committed; the server pushes the new version instead.

**Project settings** (gear in the top bar, a tab): *extra excludes* (`.gitignore` syntax, applied after the built-ins and the `.gitignore`; they cannot re-include a built-in) and *Apply server changes automatically* (default on). Stored on the server per repo (`project.settings.get/set`); unknown keys are kept.

## What is never sent

- Built in (a `.gitignore` cannot re-include them): `.git` (the folder, or a submodule's `.git` file), `node_modules/`, `bin/`, `obj/`, `.vs/`, `.idea/`, `.env`, `.env.*`, `*.pfx`, `*.key`, `*.pem`, `id_rsa*`.
- Everything the root `.gitignore` excludes. Supported subset: `#` comments, blank lines, `*`, `**`, `?`, trailing `/`, leading `/`, `!`. **Not supported:** character classes `[...]` and backslash escapes. Matching ignores case. A `.gitignore` the browser cannot read stops the pass (nothing is sent), so ignored files are never uploaded by mistake.
- The walk does not enter the folders the `.gitignore` excludes with a rule without wildcards (`name`, `name/`, `/path/`, `**/name/`) that no later `!` rule could re-include (git cannot re-include anything under an excluded folder anyway). Folders excluded only by a wildcard rule (`build-*/`, `*.tmp/`) are still walked, and their files dropped one by one.
- Files larger than 20 MB (listed as "too large"). A folder with more than 20 000 files to sync is refused with a message.
- Paths the server would refuse; they are shown as errors and never sent:
  - a `.git` segment (any case): git metadata is never synced, and the server refuses it too;
  - a segment longer than 237 characters (so `<name>.<8-character session tag>.aicp-tmp` fits in 255);
  - 8.3-style short names, i.e. a `~` followed by a digit (`GIT~1`, `PROGRA~1`), which Windows can expand to another folder;
  - Windows device names (`CON`, `NUL`, `COM0`–`COM9`, `COM¹`, `LPT0`…, `CONIN$`, …), a trailing dot or space, `:`, `\`, `..`;
  - Unicode format characters (such as U+202E, U+200B), unpaired surrogates, and lookalikes of `/`, `\`, `.` and `:` (such as U+FF0F, U+2215);
  - the temp suffix `.aicp-tmp` in any segment.

## Deletes on the mirror

A file on the mirror is deleted only when a scan positively saw it absent. A file the browser could not read, a folder it could not list, or a file larger than 20 MB is **never deleted**: the full manifest lists it in `keep` (a folder as a prefix ending in `/`), deltas leave it alone, the mirror keeps its last synced version and the explorer shows an error or the "too large" badge. Consequences:

- If the folder looks empty (or access was lost) while files are known, the pass is refused and nothing is deleted.
- After a **page reload** the first pass sends the full manifest with its keep list, so files deleted while the page was closed disappear from the mirror at once, even while something else is unreadable.
- A file whose upload failed and that is then deleted is deleted on the mirror too. A kept file (unreadable, too large) that is later deleted is deleted on the mirror with the next scan.
- Every 10 minutes the full manifest is sent again, so nothing a delta could not see stays out of sync; a server edit is no longer overwritten by it, it is pushed back (see [Back channel](#back-channel)).
- A file whose upload keeps failing is retried only after it changes, or after **5 minutes**; meanwhile it shows its error and is not re-sent every 10 s. A transient server error (`internal`) is retried on the next scan.
- A reconnect in the middle of an upload ends the pass; the next scan starts over. Losing access to the folder in the middle of the uploads ends the pass too ("Access to the folder was lost; click Restore access."), without marking the remaining files as failed.

## Known limitations

- **Case-only rename:** renaming `readme.md` to `README.md` changes nothing on the mirror, which keeps the old casing (it is case-insensitive and already has the content).
- **Same folder name:** two folders with the same name (after sanitizing) share one mirror folder, and each sync deletes the other's files. Open one at a time, or rename a folder.
- **Check-then-write window:** the browser writes a pushed change if its file is still the agreed version; a save in the few milliseconds between that check and the write is lost.
- **Upload commit window:** a mirror file changing in the milliseconds between the server's check and the commit of an upload is not detected (the next pass pushes it as a conflict candidate).
- **Conflicts show no diff:** the Conflict tab previews only the server's version as text (up to 256 KB); a diff view comes with the code navigator.
- One folder at a time.

## Where the mirror lives

`Mirror:Root`, one sub-folder per synced folder (`<root>\<repo>`, the folder name reduced to `[A-Za-z0-9._-]`, at most 64 characters):

| Server runs as | Default `Mirror:Root` |
|---|---|
| Windows service (or with `AICP_DATA_DIR`) | `<DataDir>\mirror`, i.e. `%ProgramData%\AiChromeProxy\mirror` as a service |
| `dotnet run` without a data directory | `src\AiChromeProxy.Server\data\mirror` (gitignored; `data/mirror` under the content root) |

Set `Mirror:Root` (or the environment variable `Mirror__Root`) to put it elsewhere. Only the default location inside the data directory gets the data directory's protected DACL (see [Windows host](windows-host.md)); a custom `Mirror:Root` outside it keeps whatever permissions its folder has, so restrict it yourself (other local users could otherwise read the synced code, or plant files Claude will act on). Every protocol path is validated (relative, `/`-separated, normalized, checked by the rules above) and must resolve inside `<root>\<repo>` exactly as written (a path that Windows would rewrite, for example an 8.3 expansion, is refused); the server never follows a link or junction inside the mirror.

### Long paths on Windows

A synced path may be up to 260 characters (relative to the folder), so on the mirror `<root>\<repo>\<path>` can pass the classic 260-character `MAX_PATH` limit (the default root alone, `C:\ProgramData\AiChromeProxy\mirror\`, takes 36). The Server itself handles long paths; the tools that run in the mirror (git, builds, later Claude) may not:

- **Enable Win32 long paths** (as administrator): Group Policy *Computer Configuration → Administrative Templates → System → Filesystem → Enable Win32 long paths*, or `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1` (DWORD):
  ```powershell
  New-ItemProperty -Path HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem -Name LongPathsEnabled -Value 1 -PropertyType DWord -Force
  ```
  A process reads it when it starts, so no service restart is needed: tools started afterwards (new git processes) see it. Programs without the long-path opt-in in their manifest still stop at 260.
- **git:** `git config --system core.longpaths true` (as administrator).
- **A short `Mirror:Root`** such as `C:\m` shortens every path. Set it in `%ProgramData%\AiChromeProxy\appsettings.json` (`"Mirror": { "Root": "C:\\m" }`) or as the machine environment variable `Mirror__Root`, then restart the service. Prefer the `appsettings.json` file: a machine-level environment variable reaches a Windows service only after a reboot. Outside the data directory the mirror gets **no** protected DACL, and standard users may create folders in `C:\`: create the folder yourself first and restrict it, for example (as administrator):
  ```powershell
  # The service account is the SERVICE_START_NAME of the service, not $env:USERNAME (under over-the-shoulder UAC that is the approving admin).
  # The service account (the tray user), e.g. .\jane or DESKTOP-1\jane:
  $svc = ((sc.exe qc AiChromeProxy) -match 'SERVICE_START_NAME') -replace '^.*:\s*', ''
  if (Test-Path C:\m) { (Get-Acl C:\m).Owner; throw 'C:\m exists and you did not create it: pick another name' }
  New-Item -ItemType Directory C:\m -ErrorAction Stop
  icacls C:\m /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${svc}:(OI)(CI)M"
  ```
  If the folder already exists and you did not create it, pick another name: someone else may own it and keep control of its ACL.
  Moving or emptying the mirror makes the browser upload everything again (nothing in the folder is deleted).
- **Not `subst` or mapped network drives:** they exist per logon session, and the service (which runs without anyone logged on) does not see them.
- **Segment limit:** a single file or folder name stays limited to 237 characters (NTFS allows 255, minus the temp suffix `.<tag>.aicp-tmp`); no setting lifts it.

## Troubleshooting

- **The page still behaves like the previous version after an update.** The Server sends the page (`index.html`) with `Cache-Control: no-store` and points it only at fingerprinted files (`blazor.webassembly.<hash>.js`, `app.<hash>.css`, and through an import map `dotnet.<hash>.js` and `fsaccess.<hash>.js`), so a new version is picked up on the next load even when a proxy caches the other files. A tab opened before the update shows the **A new version is installed — Reload** banner; click **Reload**, or press **Ctrl+Shift+R**. On Cloudflare, also set *Caching → Configuration → Browser Cache TTL* to **Respect Existing Headers** (see [Remote access](windows-host.md#remote-access)).

## Manual checklist (Chrome on the locked-down machine)

The browser code (`fsaccess.ts`) has no automated tests; run this before a release that touches sync. Use a test repository, not production code, the first time.

**Real use**

- [ ] Open `https://<your host>` (e.g. `https://code.example.com`), sign in through Cloudflare Access, click **Open folder**, pick a real repository. Note the first-sync time and the file count in the left status box ("Synced N files").
- [ ] The picker opens on the very first click after the page loads (no "user activation" error).
- [ ] On the home server the mirror folder has as many files as the explorer counts as synced (`(Get-ChildItem <mirror>\<repo> -Recurse -File).Count`), and a few spot-checked files are identical (`Get-FileHash`).
- [ ] Edit a file → the mirror has the new content within ~10 s.
- [ ] Add a file, delete a file, rename a file → the mirror follows within ~10 s; the deleted/renamed file is gone and empty folders are removed.
- [ ] `.env` and `node_modules` exist in the folder but not in the mirror.
- [ ] Reload the page → the tree shows **Restore access**; one click resumes the sync without picking the folder again, and nothing is deleted on the mirror.
- [ ] Turn Wi-Fi off for 1 minute → the pill shows "Reconnecting…"; after reconnecting it shows "Connected", the sync status returns to "Synced", and an edit made while offline reaches the mirror.
- [ ] Theme toggle cycles system → light → dark and text is readable in both; the left and right panels resize and collapse, keep their content when reopened, and the tree works with the keyboard (arrows, Enter).
- [ ] The Actions history lists the pass, uploads, deletes and the reconnect; the server log has one "Sync <repo>: manifest of …" line per pass and "Sync <repo>: stored …" after uploads, and no file contents.

**Browser edge cases**

- [ ] IndexedDB blocked (site data blocked, or a private window): picking a folder still shows its name and scan, hash and upload work (without the cache); after a reload there is no restore, only **Open folder**, and no hang or error.
- [ ] Revoke access (site settings → File System → remove), then focus the tab: the tree says "Access to the folder was lost; click Restore access." and the mirror is unchanged.
- [ ] Make a subfolder unreadable (deny Read on `sub\`): the Errors tab shows "sub/: Could not list this folder…" and the mirror keeps `sub\*`.
- [ ] Lock a synced file exclusively (e.g. `[IO.File]::Open(path,'Open','Read','None')` in PowerShell): it shows "Could not be read; the mirror keeps the last synced version." and stays on the mirror, also after a reconnect or reload.
- [ ] Make the root `.gitignore` unreadable: the pass stops with "The folder's .gitignore could not be read…" and nothing is sent.
- [ ] Nest folders more than 64 levels deep: the deepest folder is reported as not listed and nothing under it is deleted.
- [ ] A file larger than 20 MB is listed with the "too large" badge and is not in the mirror; growing a synced file past 20 MB does not delete its mirror copy, also after a reload or reconnect.
- [ ] Close the tab, delete a synced file, lock another one exclusively, reopen and **Restore access**: the deleted file disappears from the mirror within ~10 s, the locked one stays.
- [ ] Add a large folder to the root `.gitignore` (e.g. `dist/` with thousands of files): the next scan is about as fast as before it existed, and nothing from it reaches the mirror.
- [ ] A repository with a submodule: its `.git` file is neither listed as an error nor synced.
- [ ] Leave the page open 10+ minutes, put an extra file into the mirror folder on the server: it appears in the folder (the back channel), it is not deleted.
- [ ] Revoke access (site settings → File System) while a first sync of many files uploads: the pass stops with "Access to the folder was lost; click Restore access."; after **Restore access** the remaining files upload at once (not after 5 minutes).
- [ ] Change a file while it uploads (a large file, edit and save mid-upload): the upload fails once and the next scan uploads one consistent version.
- [ ] Switch to another tab and back, and focus the window: one rescan per event, none while the tab is hidden, and none after the app is closed.
- [ ] Delete the folder's contents (or move the folder away) while the page is open: the pass is refused ("The folder looks empty; nothing was deleted…") and the mirror keeps its files.
- [ ] The error count in the left status box opens the Errors tab.

**Back channel**

- [ ] Edit a file on the server (with write access granted): it changes in the folder within ~5 s; delete a file on the server: it is deleted in the folder.
- [ ] Edit the same file in the folder and on the server before a sync: the **Conflict** tab opens; **Keep mine** leaves the folder's version (and the mirror gets it), **Take server's** overwrites the folder file. Repeat for each button.
- [ ] Run `dotnet build` in the mirror: `bin/` and `obj/` stay there and are not pushed to the folder.
- [ ] Without write access (new tab, no permission): the Explorer shows "N server changes — Allow writing"; after **Allow writing** the changes are applied.
- [ ] Turn **Apply server changes automatically** off in Project settings: a server edit waits ("N server changes waiting — Apply all") until **Apply all**.
- [ ] Add an extra exclude in Project settings (e.g. `*.log`): such a file on the mirror is neither pushed nor deleted.
- [ ] Restart the service: no conflict appears and nothing is overwritten (the bases persist in `aicp.db`).
