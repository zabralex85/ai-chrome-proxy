# Sub-project 3a — One-way sync and the app shell

Date: 2026-10-03. Parent: [architecture](2026-10-02-architecture-design.md) (sections "Sync flow", "Required follow-ups from the skeleton review"). Sub-project 3 is split: **3a** (this spec) mirrors a folder from Chrome to the home server and replaces the bare status page with the app shell; **3b** (later) adds the back channel — server-side edits written to the client under the hash-guard, and the conflict UI. 3b is needed only from the Claude chat sub-project on.

## Goal

In Chrome on the locked-down machine the user clicks **Open folder**, picks the repository folder, and it appears on the home server as a mirror within seconds; edits in the folder reach the mirror within ~10 s. The UI looks like a small VS Code: title bar, activity bar, explorer with the synced tree, editor area, status bar, dark and light theme.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Split | 3a one-way (client → mirror); 3b back channel + conflicts. | Each half ships and is tested on its own; nothing writes to the mirror besides sync until Claude arrives. |
| Folder access | File System Access API in a small `fsaccess.js`: `showDirectoryPicker({mode:"read"})`, handle kept in IndexedDB, after reload only `requestPermission` (one click). | Only JS can touch the API; read-only is enough for 3a (3b asks for `readwrite`). |
| Walk and hash | `fsaccess.js` walks the tree (skipping the built-in excluded directory names while walking) and returns `{path, size, mtime}`; hashing is SHA-256 via `crypto.subtle` in JS, only for files whose `size`/`mtime` differ from the cache kept in IndexedDB. | WebCrypto is far faster than hashing in WASM; the cache keeps a 20k-file rescan cheap. |
| Excludes | Built-in: `.git/`, `node_modules/`, `bin/`, `obj/`, `.vs/`, `.idea/`, `.env`, `.env.*`, `*.pfx`, `*.key`, `*.pem`, `id_rsa*`. Plus the root `.gitignore` (common subset: `#` comments, blank lines, `*`, `**`, `?`, trailing `/`, leading `/`, `!` negation). Matching is pure C# in the Client (`SyncEngine`). | Secrets never leave the machine by default; `.gitignore` covers the rest. |
| Change detection | Rescan every 10 s while the tab is visible (walk + metadata; hashing only for changed files); immediate rescan on focus. `FileSystemObserver` later (ponytail: polling, switch when the API is stable). | One code path; cheap enough for < 20k files. |
| Limits | Files > 20 MB are skipped and listed as "too large" in the UI; at most 20 000 files (beyond: refuse with a clear message). | Architecture constraint (< 20k files); keeps memory and transfer bounded. |
| Protocol | Envelopes over the existing hub (camelCase, compatible additions only): `sync.open {repo}` → `sync.manifest {repo, entries[{path,size,sha256}]}` (sent in pages of ≤ 500 entries, last page `final:true`) → server replies `sync.need {repo, paths[]}` and deletes mirror files absent from the manifest → client sends `sync.chunk {repo, path, offset, data(base64), last, sha256?}` (≤ 16 KB raw per chunk) → server `sync.stored {repo, path}` or `error`. Later scans send only changes: `sync.delta {repo, upserts[], deletes[]}`. | Stays under SignalR's 32 KB message limit (architecture follow-up); paging keeps the manifest under it too. |
| Mirror | `Mirror:Root`; default `<DataDir>\mirror` when a data directory is in use (service), else `data/mirror` under the content root (dev; gitignored). One folder per repo: `<root>\<repo>` where `repo` is the picked folder's name, sanitized (`[A-Za-z0-9._-]`, max 64). Files are written to `<path>.aicp-tmp` and moved into place after the whole content arrived and its SHA-256 matched. | Protected location as a service; atomic per file; hash verified end to end. |
| Paths | Every protocol path: relative, `/`-separated, no empty / `.` / `..` segments, no `\`, no `:` (drive or ADS), no reserved Windows names, no trailing dot or space, ≤ 260 chars; resolved path must stay inside `<root>\<repo>`. Shared validator in Domain (pure, tested); the browser applies it too. | Architecture security rule. |
| Handler contract | Follow-up from the skeleton review: `IEnvelopeHandler.HandleAsync(Envelope, EnvelopeContext ctx, ct)`; `ctx` carries the connection id, the Access email (null in Development without Access) and `SendAsync` for replies/pushes. Hub catches handler failures → `error {code, message?}` with the request's `correlationId` (`bad_request`, `not_found`, `too_large`, `internal`). Client gets `RequestAsync(envelope, timeout)` that owns correlation. | Sync needs server→client replies and clean errors. |
| Reconnect | Client retries forever (1, 2, 5, 10, then every 30 s); after a reconnect it re-sends the full manifest. | Long sessions over a home link (architecture follow-up). |
| Concurrency | Server processes one sync session per connection; uploads are sequential per client (simple, bounded memory). | ponytail: sequential uploads, parallelise if the first sync of large repos is too slow. |
| UI stack | Plain Blazor components + one CSS file with design tokens (CSS custom properties) for dark/light (`prefers-color-scheme`, plus a toggle stored in `localStorage`); inline SVG icons; no component library. | Small, fast, no dependency; Monaco/mermaid arrive in later sub-projects. |

## UI (the shell)

- **Title bar:** app name, picked folder name, connection pill (Connected / Reconnecting… / Offline) with ping latency on hover; theme toggle.
- **Activity bar:** Explorer (active); Chat and Search icons shown disabled with "coming soon" tooltips.
- **Explorer sidebar:** **Open folder** button (or the folder name with **Change** and a **Restore access** button when permission is lost); sync summary (files synced / total, bytes, last sync time, progress bar while uploading); the file tree from the manifest (collapsible folders, file icons by extension, sync badge per file: synced / pending / too large / error); excluded items are not shown.
- **Editor area:** welcome page when nothing is selected (what the app does, the three steps: open folder → wait for sync → chat soon); selecting a file shows its metadata (path, size, hash, sync state) — the viewer comes with the code navigator sub-project.
- **Status bar:** connection state, sync state ("Synced 1 234 files", "Uploading 12/80", "Rescan in 7 s"), errors count with a click-to-open list.
- Keyboard: tree navigable with arrows/Enter; focus styles visible; colours meet WCAG AA contrast in both themes.
- Layout works from 1024 px wide; the sidebar is resizable (drag) and collapsible.

## Server

- `SyncHandlers` in Application (ports: `IMirrorStore` for file operations, implemented in Infrastructure on the file system).
- Per connection session: current repo, received manifest pages, pending uploads (path → temp file, expected size/hash, bytes received).
- On the final manifest page: compute `need` (missing or different hash; the mirror's hashes are computed once and cached in memory per repo keyed by path+size+mtime), delete mirror files not in the manifest (only regular files under `<root>\<repo>`, never following links), remove empty directories.
- Logging: per sync session a summary line (files, bytes, duration); never file contents.

## Testing

xunit (CI): path validator (accept/reject table incl. Windows edge cases); `.gitignore` matcher (table of patterns); manifest diff and paging; chunk assembly (order, offsets, size and hash checks, temp file cleanup on failure); mirror deletion never leaving `<root>\<repo>` (junction inside the mirror is not followed); handler/error contract (unknown type, exception, correlation); client `RequestAsync` timeout; reconnect re-sends the manifest (fake transport). Server hub end-to-end with `WebApplicationFactory` for one small repo (open → manifest → need → chunks → stored → files on disk under a temp mirror root).
E2E (local, Playwright): the shell renders (title bar, explorer, status bar, theme toggle); the folder picker cannot be automated — sync is exercised in the hub test, and manually in the checklist.
Manual checklist (`docs/sync.md`): pick a real repo in Chrome; first sync time and file count; edit/add/delete/rename a file → mirror follows within ~10 s; `.env` and `node_modules` not on the server; reload → **Restore access**; disconnect Wi-Fi 1 min → reconnects and resyncs; > 20 MB file shown as too large.

## Done when

- A real repo picked in Chrome over `https://<host>` is mirrored on the home server and stays in sync.
- The shell UI replaces the status page; E2E covers it.
- CI green; gate ≥ 85%.

## Out of scope (3b and later)

Writing to the client, hash-guard, conflict UI (3b); file viewer/Monaco, search (code navigator); chat (Claude chat); multiple folders open at once; `FileSystemObserver`; block-level deltas.
