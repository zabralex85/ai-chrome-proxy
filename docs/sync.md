# Sync (one way: browser → home server)

The folder you open in Chrome is mirrored to the home server and kept in sync. Nothing is written back to the folder yet (that is sub-project 3b). Design: [spec](superpowers/specs/2026-10-03-sync-and-shell-design.md).

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

- **Top bar:** app name, folder name, connection pill (Connected / Connecting… / Reconnecting… / Offline; the tooltip shows the ping latency), theme toggle (system → light → dark, kept in `localStorage`), panel toggles, and the user: the Cloudflare Access email, or "local" when there is none (Development).
- **Left:** **Open folder** (or the folder name with **Change**); when access is lost or the server refuses the folder, a notice with **Restore access** and **Change folder**. The tree shows a sync badge per file (synced, pending, too large, error). At the bottom the **sync status**: "Synced 1 234 files · Rescan in 7 s", "Uploading 12/80", "Access needed"; the error count opens the **Errors** tab.
- **Centre:** tabs (a permanent Welcome tab, one closable tab per opened file showing its metadata, the Errors tab), the main area, and a disabled chat input (Claude chat arrives in a later sub-project).
- **Right:** **Actions history**, newest first, at most 500 entries: folder opened, pass started/finished (files, bytes, duration; "Already in sync" when nothing changed), uploads and deletes, errors with the server's message, reconnects, access lost. At the bottom the **connection status** with the ping latency (measured every 15 s).
- Both side panels resize (drag or arrow keys on the separator) and collapse; collapsed panels keep their state. The tree and tabs work with the keyboard (arrows, Home/End, Enter).

## How it works

1. **Open folder** calls `showDirectoryPicker({mode: "read"})` (`Scripts/fsaccess.ts`). The folder handle is kept in IndexedDB (best effort: without IndexedDB everything still works, only the hash cache and **Restore access** are lost). After a reload Chrome asks again with one click (**Restore access**).
2. Every 10 s while the tab is visible, and at once when it gets focus, the browser reads the root `.gitignore`, walks the folder (without entering the built-in excluded folders and the folders the `.gitignore` excludes), drops excluded files, hashes new or changed files (SHA-256 via WebCrypto, cached by size and modification time) and tells the server what changed. Only one scan runs at a time.
3. The server answers with the paths it is missing; the browser uploads them in 16 KB chunks, one file at a time. Each file is written to `<path>.<session>.aicp-tmp` (a temp name per connection, so a reconnect never collides with the old connection's unfinished upload) and moved into place only after its SHA-256 matched.

| Message (`Envelope.type`) | Direction | Payload (camelCase) |
|---|---|---|
| `sync.open` → `sync.opened` | client → server → client | `{repo}` — the folder name; the reply carries the sanitized name |
| `sync.manifest` → `sync.need` | client → server → client | `{repo, entries[{path, size, sha256}], final, keep?[]}` (pages of ≤ 500 entries and ≤ 24 000 bytes; `keep` pages follow the entry pages and count toward the same limits) → `{repo, paths[]}`. `keep` (optional) lists what the browser could not sync but the mirror must keep: file paths, and folder prefixes ending in `/`; nothing is uploaded for them |
| `sync.delta` → `sync.need` | client → server → client | `{repo, upserts[{path, size, sha256}], deletes[]}` → `{repo, paths[]}` |
| `sync.chunk` → `sync.stored` | client → server → client | `{repo, path, offset, data, last, sha256?}` → `{repo, path}` (only the last chunk is answered; when an earlier chunk of the upload failed, the rest is dropped and the last chunk gets that first error). `data` is **base64url without padding** (`-` and `_`, no `+`, `/` or `=`), at most 16 KB raw, so a chunk stays well under SignalR's message limit |
| `error` | server → client | `{code, message?}` with the request's `correlationId`; `code` is `bad_request`, `not_found`, `too_large`, `unknown_type` or `internal` |

After the last manifest page the server deletes mirror files that are neither in the manifest nor covered by `keep` (an exact path or a kept folder prefix, ignoring case; an invalid `keep` entry is refused with `bad_request`). The browser sends the full manifest on every new session (first sync, page reload, reconnect, folder change) and then every 10 minutes (cheap: the server caches hashes), deltas in between. The server refuses (`bad_request`) an empty manifest (no entries and no `keep`), and a delta that would delete every file, while the mirror has files: an empty folder over a non-empty mirror is never applied (the explorer shows the message with **Change folder** and **Restore access**). More than 20 000 files waiting for upload is refused with `too_large`.

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
- Every 10 minutes the full manifest is sent again, so nothing a delta could not see (for example a file added to the mirror on the server) stays there for long.
- A file whose upload keeps failing is retried only after it changes, or after **5 minutes**; meanwhile it shows its error and is not re-sent every 10 s. A transient server error (`internal`) is retried on the next scan.
- A reconnect in the middle of an upload ends the pass; the next scan starts over. Losing access to the folder in the middle of the uploads ends the pass too ("Access to the folder was lost; click Restore access."), without marking the remaining files as failed.

## Known limitations

- **Case-only rename:** renaming `readme.md` to `README.md` changes nothing on the mirror, which keeps the old casing (it is case-insensitive and already has the content).
- **Same folder name:** two folders with the same name (after sanitizing) share one mirror folder, and each sync deletes the other's files. Open one at a time, or rename a folder.
- One folder at a time; one-way only (edits made on the server are overwritten or deleted by the next sync until 3b adds the back channel).

## Where the mirror lives

`Mirror:Root`, one sub-folder per synced folder (`<root>\<repo>`, the folder name reduced to `[A-Za-z0-9._-]`, at most 64 characters):

| Server runs as | Default `Mirror:Root` |
|---|---|
| Windows service (or with `AICP_DATA_DIR`) | `<DataDir>\mirror`, i.e. `%ProgramData%\AiChromeProxy\mirror` as a service |
| `dotnet run` without a data directory | `src\AiChromeProxy.Server\data\mirror` (gitignored; `data/mirror` under the content root) |

Set `Mirror:Root` (or the environment variable `Mirror__Root`) to put it elsewhere. Only the default location inside the data directory gets the data directory's protected DACL (see [Windows host](windows-host.md)); a custom `Mirror:Root` outside it keeps whatever permissions its folder has, so restrict it yourself (other local users could otherwise read the synced code, or plant files Claude will act on). Every protocol path is validated (relative, `/`-separated, normalized, checked by the rules above) and must resolve inside `<root>\<repo>` exactly as written (a path that Windows would rewrite, for example an 8.3 expansion, is refused); the server never follows a link or junction inside the mirror.

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
- [ ] Leave the page open 10+ minutes, put an extra file into the mirror folder on the server: it is deleted within ~10 minutes (the periodic full manifest).
- [ ] Revoke access (site settings → File System) while a first sync of many files uploads: the pass stops with "Access to the folder was lost; click Restore access."; after **Restore access** the remaining files upload at once (not after 5 minutes).
- [ ] Change a file while it uploads (a large file, edit and save mid-upload): the upload fails once and the next scan uploads one consistent version.
- [ ] Switch to another tab and back, and focus the window: one rescan per event, none while the tab is hidden, and none after the app is closed.
- [ ] Delete the folder's contents (or move the folder away) while the page is open: the pass is refused ("The folder looks empty; nothing was deleted…") and the mirror keeps its files.
- [ ] The error count in the left status box opens the Errors tab.
