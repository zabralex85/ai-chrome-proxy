# Tree Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A context menu in the file tree to create files and folders, rename and delete, acting on the browser folder and syncing to the mirror.

**Architecture:** Pure C# rules (name validation, target folder, tab path mapping, delete dialog text, menu state) in `Client/Tree/`; browser operations in `fsaccess.ts` behind new `IFolderAccess` methods; `SyncEngine` gains action methods that ask for write access, act, log and rescan at once; the shell adds the menu, inline name input and the confirm dialog.

**Spec:** [docs/superpowers/specs/2026-10-04-tree-actions-design.md](../specs/2026-10-04-tree-actions-design.md).

## Global Constraints

- **Safety:** never run the tray, Setup/Velopack, cloudflared, services or the real `claude`; never touch `%ProgramData%\AiChromeProxy` (live install; port 5180 taken — local runs on a free port, temp mirror/database). Tests under `TempRootCleanup.Root`.
- Architecture tests unchanged: Client → Domain only.
- Style: tabs; CRLF; UTF-8 without BOM (`Utf8SourceTests`); no `this.`; `_camelCase`; sorted usings; file-scoped namespaces; one type per file (an enum may share); block-form `using (...) { }`; `Async` suffix; XML docs like the surrounding code; StyleCop errors fail the build — fix code, never the ruleset. English only.
- TypeScript strict, no `any`, no hand-written `.js`; interop wrappers `[ExcludeFromCodeCoverage]` without logic; every `fsaccess.ts` path is validated segment by segment before touching handles (reuse `splitPath`/`parentOf`); the picked root itself can never be renamed or deleted.
- Envelope contract unchanged (no new messages).
- Exact copy: menu **New File**, **New Folder**, **Rename**, **Delete**; shortcuts F2 (rename), Delete (delete), Shift+F10 / ContextMenu (open menu); notes "Created; excluded from sync", "Your browser cannot rename folders", "Resolve the server change first"; dialog "Delete `path`?" / "Delete the folder `path` and its N files?" + "This cannot be undone. The server's copy is deleted at the next sync."; buttons **Cancel** (default focus) and **Delete**; validation messages: "Enter a name.", "A name cannot contain / or \\.", "'{name}' already exists here.", plus the `SyncPath.GetError` text for other invalid names.
- Fallback rename copy limit: `SyncLimits.MaxFileSize` (20 MB).
- Commands: build `dotnet build -c Release` → 0/0; class `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<NS.Class>"`; gate `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`; E2E `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E`.
- Commit only the task's files; no AI attribution.

---

### Task 1: Rules and browser operations

**Files:** `src/AiChromeProxy.Client/Tree/TreeNames.cs` (validate a new name in a folder given the sibling names → error text or null, plus an "excluded" flag via `IgnoreRules`), `TreeTargets.cs` (target folder for New File/Folder from the clicked node; rename/delete applicability), `TabPaths.cs` (map open tab paths after a rename of a file or a folder prefix, case-only rename; list tabs to close after a delete), `DeleteDialog.cs` (title/body text from path, kind and count); `Scripts/fsaccess.ts`: `createFile(path)`, `createFolder(path)`, `rename(path, newName)` (file: `move` if available else copy+remove with the 20 MB cap; folder: `move` only; case-only via a temporary name), `remove(path, recursive)`, `canRenameFolders()`, `countFiles(path)` (all files under a folder, no excludes), and `scan` returns `directories` (relative folder paths seen, excluding skipped ones); `IFolderAccess` + `JsFolderAccess` + `FakeFolder` (in-memory, folder support, failure knobs); `FolderScan.Directories` (optional, compatible); `FileTree.Build` accepts directories; tests for all pure classes and the fake.
- [ ] Commit `Tree: name rules, targets, tab mapping, folder operations`.

### Task 2: Engine actions

**Files:** `SyncEngine` (partial file `SyncEngine.TreeActions.cs`): `CreateFileAsync(folder, name)`, `CreateFolderAsync(folder, name)`, `RenameAsync(path, newName)`, `DeleteAsync(path, isFolder)`, `CountFilesAsync(folder)`; each: refuse when a remote change waits or a conflict exists for the path (or under the folder); ensure write access (ask from the click); act; log to the Actions history (with the excluded note); wake the cycle for an immediate scan; return a result (ok / error text). `SyncEngine.Directories` for the tree. Tests over `LoopbackServer` + `FakeFolder`: create → uploaded; rename → old deleted + new uploaded on the mirror; delete → mirror copy deleted after the scan; folder delete; refused while a remote change waits; no write access → asks, refused → error, nothing changed.
- [ ] Commit `Sync: create, rename and delete in the folder, then sync`.

### Task 3: Menu, inline input, dialog

**Files:** `Shell/TreeContextMenu.razor`, `Shell/TreeNameInput.razor`, `Shell/ConfirmDialog.razor`, `Shell/FileTreeView.razor` / `Explorer.razor` (right-click, Shift+F10/ContextMenu, F2, Delete; empty-space menu for the root; empty folders rendered), `MainArea.razor`/`TabSet` (rename/close tabs via `TabPaths`; a new file opens in its tab), CSS; tests for menu state logic; E2E scenarios from the spec + dark/light screenshots of the menu and the dialog to the scratchpad (`tree-*.png`), looked at; no CSP violations.
- [ ] Commit `Shell: context menu to create, rename and delete in the tree`.

### Task 4: Docs

**Files:** `docs/navigator.md` (tree actions section + checklist items), `docs/sync.md` (empty folders not mirrored; deletes are permanent), README status line.
- [ ] Commit `Docs: tree actions`.
