# Tree actions — create, rename, delete in the file tree

Date: 2026-10-04. Builds on [3a](2026-10-03-sync-and-shell-design.md) (file tree, `fsaccess.ts`), [3b](2026-10-03-back-channel-design.md) (write access, hash-guard) and [5](2026-10-04-code-navigator-design.md) (file tabs). Owner's request: a context menu in the file navigator to create folders and files, rename files and delete files.

## Goal

Right-click (or Shift+F10 / the Menu key) on a file, a folder or the empty part of the tree opens a menu: **New File**, **New Folder**, **Rename** (F2), **Delete** (Del). The action happens in the picked folder in the browser (the user's truth); the sync engine rescans at once, so the mirror follows (creates upload, deletes and renames propagate as 3a/3b deltas).

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Where | In the browser folder through `fsaccess.ts` (new `createFile`, `createFolder`, `rename`, `removeEntry`), never on the server directly. After each action `SyncEngine` scans immediately. | One source of truth; the existing sync and conflict rules carry the change to the mirror. |
| Permission | Every action needs write access: if the folder is read-only, the same click first asks (`AllowWritingAsync` — user activation), then acts. Refused → an Actions-history error, nothing changed. | Same model as the back channel. |
| Names | Entered inline in the tree row (VS Code style: an input in place; Enter commits, Esc cancels, blur cancels). Validated with the 3a rules (`SyncPath.GetError` on the resulting path: no `..`, reserved names, trailing dot/space, `:` etc.) plus: not empty, no `/` or `\` inside a name, no existing sibling with the same name ignoring case. The error shows under the input; the input stays. Names that sync would exclude (e.g. `.env`, `bin/`) are allowed — they are the user's files — and a note says "Created; excluded from sync" ("Renamed; excluded from sync" for a rename). | Instant feedback; nothing the server would refuse can be created by mistake. |
| New file / folder | Created inside the right-clicked folder (or the folder of the right-clicked file, or the root). A new file is empty and opens in its tab (Monaco viewer). A new folder appears in the tree even while empty. | Expected editor behaviour. |
| Empty folders in the tree | `scan` also returns directory paths (`FolderScan.Directories`, compatible addition); the tree builds from files plus directories. Excluded directories stay hidden as today. Empty folders are not synced (the mirror holds files only). | Without it a new folder would vanish until a file is put in it. |
| Rename | Files: `FileSystemFileHandle.move(newName)` when available; otherwise (or when `move()` rejects as unsupported: `NotSupportedError` / `InvalidModificationError`, as on some local folders) copy (read + write the new file) then remove the old one, refusing files over the 20 MB sync limit for the fallback. Folders: `FileSystemDirectoryHandle.move(newName)` only — when the browser does not support it, **Rename** on folders is disabled with the tooltip "Your browser cannot rename folders". Open tabs of renamed files (and of files inside a renamed folder) follow the new path. Rename to the same name ignoring case only (`a.cs` → `A.cs`) goes through a temporary name. | Native move keeps content and timestamps; folder copy fallback is too risky to do silently. |
| Delete | Always confirmed in an in-app dialog (not `window.confirm`): a file → "Delete `path`?"; a folder → "Delete the folder `path` and its N files?" where N counts every file inside, including excluded ones (scanned on demand), with the note "This cannot be undone. The server's copy is deleted at the next sync." Default focus on **Cancel**; **Delete** is the destructive button. Open tabs of deleted files close. | The browser API has no recycle bin; deleting is permanent in the user's folder. |
| Concurrency | An action on a path the server is changing (a waiting remote change or a conflict) is refused with "Resolve the server change first" in the menu (items disabled with that tooltip). After the write-permission prompt an action takes the sync cycle's guard (waiting up to 15 s for a running cycle, else "Sync is busy; try again in a moment."), checks the server changes again and only then acts. | Avoids racing the hash-guard; no server write lands between the steps of a rename and no scan uploads a half-done one. |
| Menu | `role="menu"` with `menuitem`s, arrow keys, Enter, Esc closes and restores focus; disabled items explain why (tooltip + `aria-disabled`). Opens at the pointer or below the focused row. | Accessibility and the owner's VS Code-like shell. |

## Testing

xunit (CI): name validation table (valid, empty, `..`, `CON`, trailing dot, `/` inside, case-insensitive duplicate, excluded name → allowed + note); target folder resolution (file → its folder, folder → itself, empty space → root); rename path mapping for open tabs (file and folder prefix, case-only rename); delete dialog text and counts; tree building with empty directories; menu item enabled/disabled state (write access, waiting remote change, conflict, folder rename unsupported); engine-level create/rename/delete through the fake folder trigger an immediate scan and the expected sync messages (upload / delete) over `LoopbackServer`.
E2E (local, Playwright with the stubbed picker): right-click → New File → type a name → Enter → file appears and opens; New Folder → appears empty; Rename (F2) a file with an open tab → tab follows; Delete a file → dialog → Delete → gone from the tree and the mirror after sync; Delete a folder → dialog shows the count; keyboard-only flow (Shift+F10, arrows, Enter, Esc); invalid name shows the error.

## Known limitations

Deleting is permanent (no recycle bin in the browser API). Folder rename depends on browser support. Empty folders are not mirrored. No drag-and-drop moves, copy/paste or multi-select in this round.

## Done when

- The four actions work from the context menu and the keyboard, with validation and a confirmation for delete.
- The mirror follows after the next scan; open tabs follow renames and close on delete.
- CI green; gate ≥ 85%; E2E green without CSP violations.
