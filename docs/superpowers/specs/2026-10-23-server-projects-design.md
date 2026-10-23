# Server projects — read-only view of folders synced from other computers

Date: 2026-10-23. Builds on [3 sync](2026-10-03-sync-and-shell-design.md) (mirror, `IMirrorStore`, chunked base64url data), [back channel](2026-10-03-back-channel-design.md) (`sync.fetch`, ignore rules), [4 Claude chat](2026-10-04-claude-chat-design.md) (`chat.open`, `chat.history`, live events) and [5 code navigator](2026-10-04-code-navigator-design.md) (`FileView`, Monaco). Owner's request: the server holds the mirrors of folders synced from several computers; from any one of them, see how the others are doing.

## Goal

The folder name in the top bar opens a list of the projects on the server: name, whether Claude is working there now, whether a browser has the folder open now, and the last chat activity. Picking another project switches the shell to a **read-only view** of it: its chat (history and live events), its file tree and its files, all read from the server. **Back to my folder** returns to the local folder, whose sync never stops meanwhile. Nothing in the view can change the other project: no sending, stopping, approving, tree actions or settings.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Transport | Three new hub request/reply pairs in a new `MirrorBrowseHandler` (Application), outside `SyncSession`: `repos.list` → `repos`, `mirror.list` → `mirror.files`, `mirror.read` → `mirror.data`. | Stays in the `Envelope` contract (the Flutter app gets it too), behind the hub's Origin and Access checks. `SyncSession` checks every message against its one open repo and owns the write paths; a read-only browser must not touch either. |
| `repos.list` | Empty payload. Reply `ReposPayload(Repos, Truncated)` with `RepoInfo(Name, LastActivity, Running, Open)`: one entry per folder directly under `Mirror:Root` whose name passes `RepoName`/`SyncPath` checks and is not a link or junction. `LastActivity`: the newest `Updated` among the repo's chat sessions, or null. `Running`: `ChatService` has a run in the repo. `Open`: at least one sync session watches the repo now (`SyncSessions` watch count > 0; the asking browser's own folder counts). Sorted by `LastActivity` descending (nulls last), then name; entries are added while the serialized list stays under `SyncLimits.MaxPageBytes`, the rest are cut off with `Truncated = true`. | One small reply answers "how are things there". With `RepoName.MaxLength` = 64 a page holds well over 150 repos; paging a list of a few projects is not worth a cursor. |
| `mirror.list` | `MirrorListPayload(Repo, After)` → `MirrorFilesPayload(Repo, Files, Next)` with `MirrorFile(Path, Size, Modified)` (`Modified`: last write time, UTC). Files sorted by path (ordinal); a page starts after `After` (null: from the start) and holds at most `SyncLimits.MaxPageEntries` entries and `SyncLimits.MaxPageBytes` serialized; `Next` is the last path of the page, null on the last page. Paths the repo's ignore rules exclude are left out. Unknown repo (no folder) → `not_found`. | Same page bounds as the manifest. `Size` and `Modified` let the client re-read only the files that changed. |
| `mirror.read` | `MirrorReadPayload(Repo, Path, Offset)` → `SyncDataPayload` (same record as `sync.data`): up to `SyncLimits.ChunkSize` raw bytes as base64url, `Last` when fewer than a full chunk came back (an empty last chunk at an exact boundary), `Sha256` on the last chunk. Excluded path → `bad_request` "'…' is excluded from sync."; missing file → `not_found`; negative offset → `bad_request`. | Exactly `sync.fetch`'s behaviour without the session; the client decoder is reused. |
| Ignore rules | `SyncSession.BackChannel.RulesAsync` (mirror `.gitignore` + the project's `Excludes`) moves to a shared `MirrorRules.LoadAsync(IMirrorStore, IProjectStore, repo, ct)` used by both. | One definition of what the server never serves. |
| Mirror store | `IMirrorStore` gains `ListRepos()` (repo names, as above) and `ListFileInfos(repo)` (`Path`, `Size`, `Modified` of the regular files, links skipped, like `ListFiles`). | The listing needs size and time; the existing link and path checks stay in one class. |
| Chat store | `IChatStore` gains `LastActivity()` → repo → newest `Updated`, one SQL `GROUP BY` over sessions (memory store: the same over its list). `ChatService` gains `IsRunning(repo)`. | No per-repo query while listing. |
| Writes | None of the three messages writes the mirror, `aicp.db` or the attachments folder. | Read-only by construction, not by UI only. |
| Client: switcher | `TopBar`'s folder crumb becomes a button (also shown when no folder is open, labelled "Projects on the server"). It sends `repos.list` and shows a menu: per repo the name, a dot "Claude is working" when `Running`, "online" when `Open`, and the relative time of `LastActivity` ("no chat yet" when null); the local folder's own repo is marked "this folder". `Truncated` adds a last line "More projects are not shown". Keyboard: arrows, Enter, Esc. | Owner's choice: the switch lives where the folder name is. |
| Client: view mode | `Home` keeps `_viewing` (repo name or null). Picking another repo sets it; picking "this folder" or **Back to my folder** clears it. While viewing, in the top bar after the projects button: a **Read-only** pill and **Back to my folder** (the shell grid has no row for a banner); the local `SyncEngine` keeps running; its attention state (errors, conflicts) shows on the **Back** button. | One mode for the whole shell; nothing local is stopped. |
| Client: tree | `MirrorBrowser` (Client/Projects) loads all pages of `mirror.list` and the Explorer shows `FileTree.Build(paths, null)` for it: no context menu, no sync state badges, a **Refresh** button in the header. Errors show in the Explorer instead of the tree. | Reuses the tree; empty folders are not in a file list and are not shown. |
| Client: files | `FileView` gets an optional `Mirror` (`MirrorBrowser`): set, it reads through `MirrorBrowser.ReadFileAsync` (up to 8 chunks in flight; a file listed over 5 MB is not read) and re-reads when the listing's size or time changes; the file bar shows the size and "On the server". | Two real sources; the viewer, its limits, binary detection and changed-line marks stay as they are. |
| Client: tabs | `Home` keeps one `TabSet` for the local folder and a new one per view; returning restores the local tabs. Chat links `path:line` / `path#Symbol` open the viewed repo's mirror files while viewing. | Paths of two repos never mix in one tab set. |
| Client: chat | `ChatEngine.ViewAsync(repo)` / `ViewLocalAsync()`: `chat.open` for that repo (history pages and live events as today). While viewing, the input, **Stop**, approval buttons, attachments and **New chat** are disabled with the tooltip "Read-only: open this folder on its computer to chat". Back to the local folder sends `chat.open` for the local repo again; the history catches up through `chat.history`. | `chat.open` already loads any repo by name; one subscription per connection is enough for one view at a time. |
| Client: refresh | While viewing and the tab is visible: `mirror.list` again every 30 s, when a run of the viewed repo ends (`chat.sessions` with no session running), and on **Refresh**. An open file is read again when its `Size` or `Modified` changed in the new listing; its tab shows "This file is no longer on the server" when it is gone. (`ponytail:` polling; push mirror changes through `IMirrorWatcher` if 30 s proves too slow.) | Simple and bounded: one listing per 30 s per viewing tab. |
| Settings | **Project settings** is disabled while viewing (tooltip "Read-only"). | Settings change how the other project runs. |
| Access | No new check: every authenticated client of the hub can already `chat.open` any repo name, and the deployment has one Access identity. Repo names and paths go through `FileSystemMirrorStore`'s existing checks (`RepoName`, `SyncPath`, `FullPathIn`, no links or junctions on the way). | The view only makes discoverable what the session can already reach. |

## Errors

| Case | Shown |
|---|---|
| Not connected | The switcher button is disabled ("Not connected"). |
| `repos.list` fails or times out | In the menu: "Could not load the projects: <reason>" with **Retry**. |
| Viewed repo's folder is gone (`not_found` on `mirror.list`) | Explorer: "This project is no longer on the server"; chat stays readable; **Back to my folder** works. |
| `mirror.read` `not_found` | The tab: "This file is no longer on the server". |
| Excluded path from a chat link | The tab: "Could not read the file: '…' is excluded from sync.". |
| File over 5 MB / binary | Same notes as the local viewer. |
| Connection lost while viewing | The connection pill shows Reconnecting…; after reconnect the view sends `chat.open` and `mirror.list` again (no extra banner text). |

## Known limitations (go to `docs/projects.md`)

- While viewing another project, the local folder's chat gets no live pushes (one `chat.open` per connection); its runs go on and the history catches up on return.
- Two computers with folders of the same name share one mirror and one chat (existing behaviour of `RepoName`; not changed here).
- Empty folders of the mirror are not shown.
- Files Claude created in the other project show up within 30 s, or at once with **Refresh**.

## Testing

- xunit, Application: `MirrorBrowseHandler` over fakes — `repos.list` order, `Running`/`Open`/`LastActivity`, `Truncated` cut-off; `mirror.list` paging (`After`/`Next`, entry and byte bounds), excluded paths left out, unknown repo `not_found`; `mirror.read` chunks, last chunk and hash, exact boundary, excluded `bad_request`, negative offset, missing file. `MirrorRules` shared by `SyncSession` (existing back channel tests stay green).
- xunit, Infrastructure: `FileSystemMirrorStore.ListRepos` skips links, junctions, files and invalid names; `ListFileInfos` sizes and times, links skipped. `SqliteChatStore.LastActivity` across repos.
- xunit, Domain: worst-case serialization of `repos` (64-character names, `Truncated`), `mirror.files` (longest valid paths) and `mirror.data` stays under SignalR's 32 KB.
- xunit, Client: `MirrorBrowser` over a fake transport (all pages, chunk assembly with requests in flight, 5 MB refusal, changed-entry detection); component tests like `ClaudeToolsUiTests` — switcher menu content and keyboard, view mode banner and **Back**, disabled chat input and settings, local tabs restored, `FileView` with both readers.
- E2E (Reqnroll + Playwright), `Projects.feature`: the server's mirror holds another computer's project (written by the step); the browser lists it, opens it, reads a file, cannot send, and **Back to my folder** restores its own folder. The live chat of another computer is covered by `ChatEngineTests` and the manual checklist.

## Done when

- From any computer the top bar lists every project on the server and shows another project's chat, tree and files read-only; nothing in the view can change it.
- The local folder keeps syncing while viewing; returning restores its tree, tabs and chat.
- `docs/projects.md` (new, linked from `CLAUDE.md`) describes the view and its limitations; manual checklist included.
- CI green; gate ≥ 85 %.
