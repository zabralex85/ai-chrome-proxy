# Sub-project 3b — Back channel and project settings

Date: 2026-10-03. Parent: [architecture](2026-10-02-architecture-design.md); follows [3a](2026-10-03-sync-and-shell-design.md) (one-way sync and the shell).

## Goal

Edits made on the home server's mirror (by hand now, by Claude from the chat sub-project on) reach the picked folder in Chrome within seconds, and nothing is ever lost on either side: a file changed on both sides since they last agreed becomes a **conflict** the user resolves (**Keep mine** / **Take server's**). The periodic full manifest of 3a, which today overwrites server-side edits and deletes server-only files (e.g. `bin/` and `obj/` of a build run on the mirror), reconciles instead. Each project gets settings stored on the server (extra excludes, whether server changes are applied automatically) in a small SQLite database that also keeps the sync state.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Three-way state | The server keeps a **base** per file and repo: the SHA-256 both sides last agreed on (set when an upload is committed, when a manifest or delta entry equals the mirror, and when the client acknowledges a server version). Every decision compares the client's hash **C**, the mirror's **M** and the base **B** (each may be absent). | The only way to tell "changed here" from "changed there" without clocks; survives reloads and restarts. |
| Decision table | Pure function `SyncDecision.Decide(C, M, B, baselined)` in Application: `C == M` → in sync (B := C); `M == B` → client changed → upload (or delete the mirror file when C is absent); `C == B` → server changed → **push**; otherwise → **push** as a conflict candidate (the client decides it is one, see below). Before a repo is **baselined** (its first full manifest after the upgrade) the client wins as in 3a (upload, or delete the mirror file), so an existing 3a mirror needs no migration. | One table for manifests, deltas, the file watcher and the periodic full manifest; tested as a table. |
| State store | SQLite, file `<DataDir>\aicp.db` (dev: `data\aicp.db`, gitignored; `Projects:Database` overrides), `journal_mode=WAL`, `synchronous=NORMAL`, schema version in `PRAGMA user_version`. Tables: `repo(name PK, baselined)`, `base(repo, path COLLATE NOCASE, sha256, PK(repo, path))`, `setting(repo, key, value, PK(repo, key))`. `Microsoft.Data.Sqlite` without an ORM, behind the Application port `IProjectStore` (Infrastructure `SqliteProjectStore`). Writes of one manifest page or delta go in one transaction. | Durable across the hourly update restarts (an in-memory base would let the client win after every restart); WAL as asked; no EF for three tables. |
| Detecting server edits | `FileSystemWatcher` per repo folder while at least one session has it open (Infrastructure `MirrorWatcher`, port `IMirrorWatcher`); events are coalesced for 500 ms, temp files (`.aicp-tmp`), excluded and invalid paths are dropped; a buffer overflow checks every mirror file. For each path the server decides with C = the base (the client's last agreed state) — i.e. it pushes when `M != B`. Its own writes (commit of an upload) set the base first, so they cause no push. | Seconds, not the 10-minute full manifest; comparing with the base makes "is this my own write?" bookkeeping unnecessary. |
| Excludes on the server | `IgnoreRules` moves from the Client to Domain (pure, BCL only). The server builds it from the mirror's `.gitignore` plus the project's extra excludes: excluded mirror files are **never** deleted, pushed or reported. | Build outputs and other server-only files (Claude runs `dotnet build` on the mirror) stay; secrets on either side never cross. |
| Push | Server → client `sync.remote {repo, changes[{path, sha256?, size, base?}]}` (no correlation id; `sha256` null = deleted on the mirror, `base` null = never agreed; pages ≤ 500 changes and ≤ 24 000 bytes). Sent to every connection that has the repo open, after the watcher fires and in reply-order after a manifest/delta page whose decisions pushed. Files larger than `SyncLimits.MaxFileSize` and invalid paths are not pushed (logged once on the server). | Compatible addition; the client owns the folder, so it decides what to write. |
| Content transfer | Client pulls: `sync.fetch {repo, path, offset}` → reply `sync.data {repo, path, offset, data (base64url), last, sha256?}` (≤ 16 KB raw, `sha256` of the whole file on the last chunk). The client checks the assembled size and hash against the change it was told; a mismatch (the file changed meanwhile) drops the download — the watcher pushes the newer version anyway. One file at a time (ponytail: sequential fetch; pipeline like the uploads if a large refactor is slow). | Same 32 KB message limit as the uploads; the request/reply pairs give flow control for free. |
| Hash-guard (client) | Before writing or deleting, the client hashes the file **now** (not the last scan): equal to the change's `sha256` → already there; equal to `base` (absent when `base` is null) → write the fetched content with `createWritable` (atomic swap; parent folders created) or `removeEntry`; anything else → **conflict**. Afterwards `sync.ack {repo, path, sha256?}` (the version it now has). The client also refuses a path it would exclude itself or that `SyncPath` rejects (never `.git`). | The browser is the last line of defence for the user's folder; check-then-write leaves a window of milliseconds against an editor saving the same file at the same moment (accepted; the next scan uploads such a save, the server then sees it as a conflict). |
| Acknowledge | `sync.ack {repo, path, sha256?}` sets B := `sha256` (null removes the base); reply `sync.ack` echoing the payload. Used after a write (`sha256` = what was written), and for **Keep mine** (`sha256` = the server's version the user saw — the client's own version then counts as a client change, is upserted by the next delta and uploaded; for a server-side delete the client's file is uploaded as new). | One message, one meaning ("the client has seen this server version"), covers both resolutions. |
| Conflicts (UI) | A conflict is listed in the Explorer with a conflict badge, counted in the left status, logged in the Actions history once, and opens as a **Conflict** tab: path, both sides (size, hash, "deleted"), the server version's text when it is text and ≤ 256 KB (fetched on demand), and the buttons **Keep mine** and **Take server's** (writes the server version without the guard, then acks). A file in conflict is neither uploaded nor written until resolved; a new push or local edit of it updates the tab. | Nothing is lost silently; a real diff view arrives with Monaco in the code navigator. |
| Write permission | The picker stays `mode:"read"` (proven on the locked-down machine). The first time server changes wait, the Explorer shows "N server changes — **Allow writing**", which calls `requestPermission({mode:"readwrite"})` from that click. Without write access (refused, or blocked by policy) changes keep waiting and nothing else breaks. | One-way sync never depends on a write policy that may be blocked. |
| Project settings | Per repo, on the server: `excludes` (extra `.gitignore`-syntax lines, after the built-ins and `.gitignore`; cannot re-include a built-in exclude) and `applyServerChanges` (default true; false: changes wait in the "server changes" list with **Apply** / **Apply all**). `sync.opened` carries `settings`; `project.settings.get {repo}` / `project.settings.set {repo, settings}` reply `project.settings {repo, settings}`. A **Project settings** tab (gear in the top bar while a folder is open). Unknown keys are kept (later sub-projects add the agent command, model and Claude permissions). | The user's request: per-project overrides "like a harness"; two settings that matter now prove the path end to end. |
| Connection lost | When a cycle fails because the transport is not connected, the Actions history and status say "Connection lost — reconnecting…" instead of the SignalR exception text. | Seen on the real install (3a). |

## Flows

**Server edit, client unchanged.** Watcher → `M != B` → `sync.remote {path, sha256: M, base: B}` → client: now-hash == B → fetch → write → `sync.ack {M}` → B := M; the next scan sees C == M and the client's known state is already M, so no delta.

**Both changed.** Client edits X (C2) and the server edits X (M2). Whichever arrives first, the other side's step meets a mismatch: the delta upsert of C2 reaches the server with `M2 != B` and `C2 != B` → no upload, push; the client's guard sees now-hash C2 ≠ B → conflict tab. **Keep mine** → ack M2 → B := M2 → the delta upserts C2 → `M == B` → upload. **Take server's** → fetch, write M2, ack M2.

**Periodic full manifest.** Each entry goes through the decision table, and each mirror file absent from the manifest (not kept, not excluded) does too with C absent: `M == B` → the client deleted it → delete on the mirror; B absent (created on the server) or `M != B` → push. Nothing the server changed is overwritten or deleted.

## Protocol summary (additions, camelCase)

| Type | Direction | Payload |
|---|---|---|
| `sync.opened` | S → C | `{repo, settings?}` (`settings` added) |
| `sync.remote` | S → C (push) | `{repo, changes[{path, sha256?, size, base?}]}` |
| `sync.fetch` | C → S | `{repo, path, offset}` → `sync.data` |
| `sync.data` | S → C | `{repo, path, offset, data, last, sha256?}` |
| `sync.ack` | C → S | `{repo, path, sha256?}` → echo |
| `project.settings.get` | C → S | `{repo}` → `project.settings` |
| `project.settings.set` | C → S | `{repo, settings}` → `project.settings` |
| `project.settings` | S → C | `{repo, settings{excludes, applyServerChanges, …}}` |

Errors as in 3a (`error {code, message}`); `sync.fetch` of a missing file → `not_found`.

## Testing

xunit (CI): the decision table (every C/M/B combination, before and after baseline); `SqliteProjectStore` on a temp file (schema creation, WAL, upserts per page in one transaction, settings round trip, unknown keys kept); `IgnoreRules` table moved with it plus extra excludes and "cannot re-include a built-in"; `SyncSession` manifest/delta/ack/fetch with fakes (server edit never overwritten by a periodic manifest, server-only and excluded files never deleted, baseline after the first full manifest); watcher coalescing with a fake clock and one real `FileSystemWatcher` test in a temp folder; client engine with the fake folder (guard: write, already there, conflict; Keep mine / Take server's; no write permission keeps changes waiting; `applyServerChanges` off; connection-lost message). Hub end-to-end with `WebApplicationFactory`: sync a small repo, edit a mirror file on disk → `sync.remote` → fetch → ack → base updated.
E2E (local, Playwright): Project settings tab renders and saves; conflict tab and server-changes banner render (engine state injected as in 3a's shell tests).
Manual checklist (`docs/sync.md`): edit on the server → in the folder within seconds; edit both → conflict → each button; delete on the server; `dotnet build` on the mirror → `bin/obj` stay and are not pushed; Allow writing; settings excludes.

## Known limitations

Check-then-write window (above). Two folders with the same name share one mirror and one settings row (3a). A case-only rename on the server is not mirrored (3a). Conflicts show text only; a diff view comes with the code navigator.

## Done when

- A server edit appears in the Chrome folder within ~5 s; an edit on both sides never loses either version.
- The 10-minute full manifest no longer overwrites or deletes server-side changes; build outputs on the mirror stay.
- Project settings persist across restarts; CI green; gate ≥ 85%.

## Out of scope

Chat and the agent settings (Claude chat sub-project); diff/merge editor (code navigator); `FileSystemObserver` in the browser; multiple folders at once.
