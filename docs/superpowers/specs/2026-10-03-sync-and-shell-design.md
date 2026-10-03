# Sub-project 3a — One-way sync and the app shell

Date: 2026-10-03. Parent: [architecture](2026-10-02-architecture-design.md) (sections "Sync flow", "Required follow-ups from the skeleton review"). Sub-project 3 is split: **3a** (this spec) mirrors a folder from Chrome to the home server and replaces the bare status page with the app shell; **3b** (later) adds the back channel — server-side edits written to the client under the hash-guard, and the conflict UI. 3b is needed only from the Claude chat sub-project on.

## Goal

In Chrome on the locked-down machine the user clicks **Open folder**, picks the repository folder, and it appears on the home server as a mirror within seconds; edits in the folder reach the mirror within ~10 s. The UI looks like a small VS Code: top bar, file tree with the sync status, tabs and a main area, an Actions history panel, dark and light theme (see "UI (the shell)").

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Split | 3a one-way (client → mirror); 3b back channel + conflicts. | Each half ships and is tested on its own; nothing writes to the mirror besides sync until Claude arrives. |
| Folder access | File System Access API in a small strict-TypeScript module `Scripts/fsaccess.ts` (compiled to `wwwroot/js/fsaccess.js` by `dotnet build`, no Node.js): `showDirectoryPicker({mode:"read"})`, handle kept in IndexedDB (best effort), after reload only `requestPermission` (one click). | Only browser code can touch the API; read-only is enough for 3a (3b asks for `readwrite`). |
| Walk and hash | `fsaccess.ts` walks the tree (not entering the built-in excluded directories and the directories the root `.gitignore` excludes with a wildcard-free rule no later `!` could re-include; the `.gitignore` is read before the walk) and returns `{path, size, mtime}`; files it cannot read or list are reported as `skipped`, never silently dropped; hashing is SHA-256 via `crypto.subtle` in JS, only for files whose `size`/`mtime` differ from the cache kept in IndexedDB. | WebCrypto is far faster than hashing in WASM; the cache keeps a 20k-file rescan cheap. |
| Excludes | Built-in: `.git` (folder or submodule file; a `.git` segment is also refused by the path rules), `node_modules/`, `bin/`, `obj/`, `.vs/`, `.idea/`, `.env`, `.env.*`, `*.pfx`, `*.key`, `*.pem`, `id_rsa*`. Plus the root `.gitignore` (common subset: `#` comments, blank lines, `*`, `**`, `?`, trailing `/`, leading `/`, `!` negation; character classes `[...]` and backslash escapes are not supported). An unreadable root `.gitignore` aborts the pass. Matching is pure C# in the Client (`SyncEngine`). | Secrets never leave the machine by default; `.gitignore` covers the rest. |
| Change detection | Rescan every 10 s while the tab is visible (walk + metadata; hashing only for changed files); immediate rescan on focus. `FileSystemObserver` later (ponytail: polling, switch when the API is stable). | One code path; cheap enough for < 20k files. |
| Limits | Files > 20 MB are skipped and listed as "too large" in the UI; at most 20 000 files (beyond: refuse with a clear message). A file that is unreadable, skipped, in a folder that cannot be listed, or too large is never deleted on the mirror (the last synced version stays); the browser deletes only what a scan positively saw disappear. An empty scan while files are known is refused, and the server refuses an empty manifest (or a delta deleting every file) over a non-empty mirror. The full manifest (every new session, and every 10 minutes) carries an optional `keep` list (unreadable and too large files, unlisted folders as prefixes ending in `/`) that the server never deletes; after a page reload the first pass is a full manifest, so files deleted meanwhile go at once. A file that keeps failing is retried after 5 minutes or when it changes. | Architecture constraint (< 20k files); keeps memory and transfer bounded. |
| Protocol | Envelopes over the existing hub (camelCase, compatible additions only): `sync.open {repo}` → `sync.manifest {repo, entries[{path,size,sha256}], final, keep?[]}` (sent in pages of ≤ 500 entries, last page `final:true`) → server replies `sync.need {repo, paths[]}` and deletes mirror files absent from the manifest → client sends `sync.chunk {repo, path, offset, data (base64url, no padding), last, sha256?}` (≤ 16 KB raw per chunk) → server `sync.stored {repo, path}` or `error`. Later scans send only changes: `sync.delta {repo, upserts[], deletes[]}`. | Stays under SignalR's 32 KB message limit (architecture follow-up); paging keeps the manifest under it too. |
| Mirror | `Mirror:Root`; default `<DataDir>\mirror` when a data directory is in use (service), else `data/mirror` under the content root (dev; gitignored). One folder per repo: `<root>\<repo>` where `repo` is the picked folder's name, sanitized (`[A-Za-z0-9._-]`, max 64). Files are written to `<path>.<session tag>.aicp-tmp` (one temp name per connection) and moved into place after the whole content arrived and its SHA-256 matched. | Protected location as a service; atomic per file; hash verified end to end. |
| Paths | Every protocol path: relative, `/`-separated, no empty / `.` / `..` segments, no `\`, no `:` (drive or ADS), no reserved Windows device names (incl. `COM0`, `COM¹`, `CONIN$`), no trailing dot or space, no 8.3-style short names (`~` + digit, e.g. `GIT~1`), no format or lookalike characters, no `.git` segment, each segment ≤ 237 chars (so the temp name with its 8-character session tag fits), whole path ≤ 260 chars; invalid names are not synced and are shown as errors; resolved path must equal the plain combination with `<root>\<repo>` (no Win32 rewriting) and stay inside it. Shared validator in Domain (pure, tested); the browser applies it too. | Architecture security rule. |
| Handler contract | Follow-up from the skeleton review: `IEnvelopeHandler.HandleAsync(Envelope, EnvelopeContext ctx, ct)`; `ctx` carries the connection id, the Access email (null in Development without Access) and `SendAsync` for replies/pushes. Hub catches handler failures → `error {code, message?}` with the request's `correlationId` (`bad_request`, `not_found`, `too_large`, `internal`). Client gets `RequestAsync(envelope, timeout)` that owns correlation. | Sync needs server→client replies and clean errors. |
| Reconnect | Client retries forever (1, 2, 5, 10, then every 30 s); after a reconnect it re-sends the full manifest (a reconnect during an upload pass ends the pass). | Long sessions over a home link (architecture follow-up). |
| Concurrency | Server processes one sync session per connection; uploads are pipelined: up to 16 files await their replies per client, while the server still stores one file at a time per connection (bounded memory). | Hides the round trip per file on a home link. |
| UI stack | Plain Blazor components + one CSS file with design tokens (CSS custom properties) for dark/light (`prefers-color-scheme`, plus a toggle stored in `localStorage`); inline SVG icons; no component library. | Small, fast, no dependency; Monaco/mermaid arrive in later sub-projects. |

## UI (the shell)

Layout from the owner's sketch (2026-10-03):

```
+------------------------------------------------------------------+
| folder · connection pill · theme toggle                   user ▾ |
+-----------+------------------------------------+-----------------+
| file tree | [Welcome] [file.cs] [...] tabs     | Actions history |
|           |                                    |                 |
|           |  code / chat session / diagrams    |                 |
|           |                                    |                 |
|           +------------------------------------+                 |
+-----------+  chat input                        +-----------------+
| status    |                                    | status 2        |
+-----------+------------------------------------+-----------------+
```

- **Top bar:** app name, picked folder name, connection pill (Connected / Reconnecting… / Offline, ping latency in its tooltip), theme toggle (system → light → dark, stored in `localStorage`), and on the right the user: the Access email from `GET /cdn-cgi/access/get-identity` (same origin, served by Cloudflare Access; on failure or in Development shows "local").
- **Left — file tree:** **Open folder** button (or the folder name with **Change**, and **Restore access** when permission is lost); sync summary (files synced / total, progress bar while uploading); the tree from the manifest (collapsible folders, file icons by extension, a sync badge per file: synced / pending / too large / error); excluded items are not shown. Resizable and collapsible.
- **Centre — tabs + main area:** a **Welcome** tab (what the app does; open folder → wait for sync → chat soon) and one tab per opened file (click in the tree opens or focuses its tab; tabs closable). In 3a a file tab shows the file's metadata (path, size, hash, sync state); the code view, chat sessions and diagrams arrive in later sub-projects as more tab kinds.
- **Centre bottom — chat input:** a multi-line input with a send button, disabled in 3a with the placeholder "Chat with Claude arrives in the next update".
- **Right — Actions history:** newest first, timestamped entries of what happened: folder opened, sync started / finished (n files, bytes, duration), uploaded / deleted files (grouped per sync pass), errors (with the server message), reconnects. Capped at the last 500 entries. Later sub-projects add Claude's actions here. Resizable and collapsible.
- **Left bottom — status:** sync state: "Synced 1 234 files", "Uploading 12/80", "Rescan in 7 s", "Access needed"; an error count that opens the error list in the main area when clicked.
- **Right bottom — status 2:** connection and agent state: Connected / Reconnecting… / Offline with the ping latency; later sub-projects add Claude's state (idle / working…, session cost).
- Keyboard: tree and tabs navigable with arrows/Enter; visible focus; WCAG AA contrast in both themes. Works from 1024 px wide.

## Server

- `SyncHandlers` in Application (ports: `IMirrorStore` for file operations, implemented in Infrastructure on the file system).
- Per connection session: current repo, received manifest pages, pending uploads (path → temp file, expected size/hash, bytes received).
- On the final manifest page: compute `need` (missing or different hash; the mirror's hashes are computed once and cached in memory per repo keyed by path+size+mtime), delete mirror files not in the manifest (only regular files under `<root>\<repo>`, never following links), remove empty directories.
- Logging: per sync session a summary line (files, bytes, duration); never file contents.

## Testing

xunit (CI): path validator (accept/reject table incl. Windows edge cases); `.gitignore` matcher (table of patterns); manifest diff and paging; chunk assembly (order, offsets, size and hash checks, temp file cleanup on failure); mirror deletion never leaving `<root>\<repo>` (junction inside the mirror is not followed); handler/error contract (unknown type, exception, correlation); client `RequestAsync` timeout; reconnect re-sends the manifest (fake transport). Server hub end-to-end with `WebApplicationFactory` for one small repo (open → manifest → need → chunks → stored → files on disk under a temp mirror root).
E2E (local, Playwright): the shell renders (top bar, user, file tree, tabs, main area, disabled chat input, Actions history, sync and connection status), theme toggle, panel collapse, error list; the folder picker cannot be automated — sync is exercised in the hub test, and manually in the checklist.
Manual checklist (`docs/sync.md`): pick a real repo in Chrome; first sync time and file count; edit/add/delete/rename a file → mirror follows within ~10 s; `.env` and `node_modules` not on the server; reload → **Restore access**; disconnect Wi-Fi 1 min → reconnects and resyncs; > 20 MB file shown as too large; the browser-code edge cases (IndexedDB blocked, revoked access, unreadable folder or file, locked file, unreadable `.gitignore`, > 64 levels, file changing during upload, picker on first click, visibility/focus).

## Known limitations

A case-only rename keeps the old casing on the mirror. Two folders with the same name share one mirror folder. Requires Chrome 123+ (`light-dark()`). The periodic full manifest no longer overwrites server edits: see the [back channel design](2026-10-03-back-channel-design.md).

## Done when

- A real repo picked in Chrome over `https://<host>` is mirrored on the home server and stays in sync.
- The shell UI replaces the status page; E2E covers it.
- CI green; gate ≥ 85%.

## Out of scope (3b and later)

Writing to the client, hash-guard, conflict UI (3b); file viewer/Monaco, search (code navigator); chat (Claude chat); multiple folders open at once; `FileSystemObserver`; block-level deltas.
