# Diagram Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chat diagrams get a toolbar to open a full-window zoomable viewer, save the source / SVG / PNG into the project folder, and download SVG / PNG.

**Architecture:** Pure C# naming rules (`Client/Chat/DiagramFiles.cs`) and a gated write (`SyncEngine.SaveFileAsync`) are tested in xUnit. `Scripts/diagrams.ts` adds the toolbar, the `<dialog>` viewer (CSS transforms), the SVG/PNG export and downloads; save clicks call back into .NET, where a Blazor save dialog asks for the path and calls the engine, which asks JS for the bytes after the write gate.

**Spec:** [docs/superpowers/specs/2026-10-04-diagram-tools-design.md](../specs/2026-10-04-diagram-tools-design.md).

## Global Constraints

- **Safety:** never run the tray, Setup/Velopack, cloudflared, services or the real `claude`; never touch `%ProgramData%\AiChromeProxy` (live install; port 5180 taken — local runs on a free port, temp mirror/database). Tests under `TempRootCleanup.Root`.
- Architecture tests unchanged: Client → Domain only. Envelope contract unchanged (no new messages).
- Style: tabs; CRLF; UTF-8 without BOM (`Utf8SourceTests`); no `this.`; `_camelCase`; sorted usings; file-scoped namespaces; one type per file (an enum may share); block-form `using (...) { }`; `Async` suffix; XML docs like the surrounding code; StyleCop errors fail the build — fix code, never the ruleset. English only; example.com only.
- TypeScript strict, no `any`, no hand-written `.js`; interop wrappers `[ExcludeFromCodeCoverage]` without logic. CSP unchanged (`img-src 'self' data:` covers the PNG export; no `blob:` images, no inline scripts).
- Exact values: default folder `docs/diagrams/`; extensions `.mmd` (source), `.svg`, `.png`; name = slugified mermaid title, else type slug + `-` + local `yyyyMMdd-HHmm`; slug: lowercase ASCII `[a-z0-9]`, other runs → `-`, trimmed of `-`, ≤ 60 chars, `diagram` if empty; type slugs: `classDiagram`→`class-diagram`, `flowchart`/`graph`→`flowchart`, `sequenceDiagram`→`sequence-diagram`, `stateDiagram`/`stateDiagram-v2`→`state-diagram`, `erDiagram`→`er-diagram`, `gantt`→`gantt`, `pie`→`pie`, `mindmap`→`mindmap`, `journey`→`journey`, `gitGraph`→`git-graph`, `timeline`→`timeline`, otherwise the keyword lowercased then slugified. Zoom 10 %–800 %, step ×1.25, arrows pan 40 px; PNG 2× capped at 8192 px longer side.
- Exact copy: buttons **Open**, **Save…** (items **Source (.mmd)**, **SVG**, **PNG**), **Download…** (items **SVG**, **PNG**), viewer **Zoom in**, **Zoom out**, **Fit**, **100%**, **Close**; dialog title "Save diagram source" / "Save diagram as SVG" / "Save diagram as PNG", buttons **Save** (primary) and **Cancel**; messages "Pick a folder first", "'{name}' already exists here.", "Saved to {path}", note "Saved; excluded from sync", history "Saved '{path}'.".
- Commands: build `dotnet build -c Release` → 0/0; class `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<NS.Class>"`; gate `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`; E2E `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E`.
- Commit only the task's files; no AI attribution.

---

### Task 1: Names and the gated save

**Files:** `src/AiChromeProxy.Client/Chat/DiagramFiles.cs` (`enum DiagramFileKind { Source, Svg, Png }`; `static string Extension(DiagramFileKind)`; `static string DefaultName(string source, DateTime localNow)` (no folder, no extension); `static string DefaultPath(string source, DiagramFileKind kind, DateTime localNow)` = `docs/diagrams/` + name + ext; `static string WithExtension(string path, DiagramFileKind kind)` — trims, replaces any extension after the last `/` segment's last `.` with the kind's (adds it when none)), `src/AiChromeProxy.Client/Sync/SyncEngine.Save.cs` (`Task<TreeActionResult> SaveFileAsync(string path, Func<Task<byte[]>> content)`: path checked with `SyncPath.GetError` and `TreeNames` rules for its name in its folder; refuses when `folder.HashNowAsync(path)` finds a file ("'{name}' already exists here."); goes through the same gate as tree actions (`ActAsync`); inside the act: `await content()` then `folder.WriteAsync`; logs "Saved '{path}'."; excluded → note "Saved; excluded from sync"); `FakeFolder` support if needed; tests `DiagramFilesTests`, `SyncEngineSaveTests` (cases from the spec's Testing section).
- [ ] Tests first → fail → implement → pass; gate green.
- [ ] Commit `Diagrams: default names and a gated save into the folder`.

### Task 2: Toolbar, viewer, export, downloads (browser)

**Files:** `Scripts/diagrams.ts` (after drawing: toolbar per diagram, error diagrams get only Save source; `render(container, callback)` — the callback (`DotNetObjectReference`) gets `Save(kind, source)` from the Save menu; **Open** shows the viewer (`<dialog>` appended to `document.body` once, `showModal()`, zoom/pan per the spec, focus back on close); viewer actions reuse the same handlers; `exportSvg(source): Promise<Uint8Array>` and `exportPng(source): Promise<Uint8Array>` exported for .NET (render with the current theme; PNG renders again with `htmlLabels: false`), downloads named by the C# rule (the toolbar asks `callback.invokeMethodAsync('Name', source)` per download, then adds the extension), `setFolderOpen(open: boolean)` toggles the Save items' disabled state + tooltip "Pick a folder first"); `Shell/JsChatView.cs` (`RenderDiagramsAsync(container, callback)`, `ExportDiagramAsync(source, kind) → byte[]`, `SetFolderOpenAsync(bool)`), `DiagramCallback` target class (`[JSInvokable] Save(string kind, string source)`, `[JSInvokable] string Name(string source)`); CSS in `app.css` (toolbar, menus, viewer, dark/light); E2E: viewer open/zoom/fit/Esc focus return, Download SVG content check, no CSP violations.
- [ ] Commit `Diagrams: toolbar, full-window viewer with zoom, SVG/PNG export and downloads`.

### Task 3: Save dialog in the shell

**Files:** `Shell/SaveDiagramDialog.razor` (native `<dialog>`, title per kind, path field prefilled with `DiagramFiles.DefaultPath`, extension enforced on submit with `WithExtension`, **Save**/**Cancel**, error under the field, busy state), `Shell/ChatView.razor` wiring (callback → open dialog; Save → `engine.SaveFileAsync(path, () => chatView.ExportDiagramAsync(source, kind))` — for Source the bytes are the UTF-8 source without calling JS; success → close, "Saved to {path}" note in the chat area with an **Open** link for `.mmd`/`.svg` via the existing `#open=` handler; folder open state → `SetFolderOpenAsync`); E2E scenarios from the spec (save source → folder + mirror; duplicate name error; save PNG signature); dark/light screenshots (`diagram-*.png` in the scratchpad), looked at.
- [ ] Commit `Shell: save diagrams into the project`.

### Task 4: Docs

**Files:** `docs/chat.md` (diagram tools section + checklist items), README status line, architecture follow-up line if the README/spec lists features.
- [ ] Commit `Docs: diagram tools`.
