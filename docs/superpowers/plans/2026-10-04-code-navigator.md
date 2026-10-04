# Code Navigator (5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Files open from the tree and from chat links in a read-only Monaco viewer (highlighting, line numbers, find, go to line, minimap, folding, themes); `path:line` and `path#Symbol` are revealed and highlighted; the viewer follows changes to the file.

**Architecture:** Pure C# pieces (text decoding, line diff, symbol search, link parsing) are tested in xUnit. The browser reads the file from the picked folder through a new `IFolderAccess.ReadFileAsync`. Monaco (AMD `min/vs`, vendored and pruned) is driven by a small strict-TypeScript module `Scripts/viewer.ts` behind `IFileViewer` / `JsFileViewer`. The shell's file tab hosts the viewer and refreshes it when the sync engine sees a new hash.

**Tech Stack:** .NET 10, Blazor WebAssembly, Monaco Editor (latest stable, MIT, vendored), strict TypeScript compiled by MSBuild, xunit v3, Reqnroll + Playwright.

**Spec:** [docs/superpowers/specs/2026-10-04-code-navigator-design.md](../specs/2026-10-04-code-navigator-design.md).

## Global Constraints

- **Safety:** never run the tray, Setup/Velopack, cloudflared, services or the real `claude`; never touch `%ProgramData%\AiChromeProxy` (a live install runs here; port 5180 is taken — local runs use a free port with temp `Mirror__Root` / `Projects__Database`). Tests use temp folders under `TempRootCleanup.Root`.
- Dependency direction (Architecture tests, unchanged): Domain ← Application ← Infrastructure ← Server; Client → Domain only.
- Style: tabs; CRLF; UTF-8 without BOM (`Utf8SourceTests`); no `this.`; `_camelCase`; sorted usings; file-scoped namespaces; one type per file (an enum may share); block-form `using (...) { }`; `Async` suffix; XML docs like the surrounding code; StyleCop errors fail the build — fix code, never the ruleset. English only; no personal paths/domains (example.com).
- TypeScript strict, no `any`; no hand-written `.js`; vendored third-party files only under `src/AiChromeProxy.Client/wwwroot/lib/` (allow-listed in `BrowserScriptTests` by folder for Monaco, file for mermaid); interop wrappers `[ExcludeFromCodeCoverage]` without logic. `MarkupString` stays limited to its current allow-list.
- CSP (`ClientPage`): keep `script-src 'self' 'wasm-unsafe-eval' <sha256 of inline scripts, LF-normalised>`, no `unsafe-inline`/`unsafe-eval` for scripts; add `worker-src 'self' blob:`; `font-src 'self' data:`; E2E fails on any CSP violation (existing guard).
- Exact values: view limit 5 MB (5 × 1024 × 1024 bytes); binary = NUL in the first 8 192 bytes or invalid UTF-8 (UTF-16 LE/BE with BOM decoded); messages "Binary file — not shown", "Too large to show ({n:0.0} MB)", "This file is no longer in the folder", "Symbol not found"; changed-line marks last 2 s; LCS cap: files over 20 000 lines mark only the first and last differing lines; at most 10 live viewer instances (LRU); `path#Symbol` symbol = `[A-Za-z_][\w.]*`; declaration keywords `class|interface|record|struct|enum|def|function|func|fn|void|public|private|protected|internal|static|const|let|var|type`.
- Commands: build `dotnet build -c Release` → 0/0; class `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<NS.Class>"`; gate `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`; E2E `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E`.
- Commit only the task's files; no AI attribution.

---

### Task 1: Pure pieces — decoding, line diff, symbol search, links

**Files:** `src/AiChromeProxy.Client/Navigator/FileText.cs` (`static FileTextResult Decode(byte[] bytes)` → `Text` or `Reason` = binary/too large; limit constant), `LineDiff.cs` (`static IReadOnlyList<int> ChangedLines(string oldText, string newText)` — 1-based line numbers in the new text that were inserted or changed; LCS over lines, cap rule), `SymbolFinder.cs` (`static int? Find(string text, string symbol)` → 1-based line), `ChatLink` handling in `src/AiChromeProxy.Client/Chat/MarkdownRenderer.cs` (`path#Symbol` like `path:line`: relative, `/`-separated, no `..`, symbol pattern; in-app link `#open=<path>#<symbol>`; `TryParseOpenLink` returns path + line or symbol), the system-prompt convention in Infrastructure `ClaudeArguments` (add "or `path#Symbol`"); tests `FileTextTests`, `LineDiffTests`, `SymbolFinderTests`, `MarkdownRendererTests` additions, `ClaudeArgumentsTests` update.
- [ ] Tests first (tables: UTF-8 with/without BOM, UTF-16 LE/BE BOM, NUL → binary, invalid UTF-8 → binary, 5 MB + 1 → too large; diff insert/delete/change/identical/empty/cap; symbol with keyword, without, whole word only, not found; links accepted/refused incl. injection attempts) → fail → implement → pass.
- [ ] Commit `Navigator: decoding, line diff, symbol search, path#Symbol links`.

### Task 2: Reading files in the browser

**Files:** `Scripts/fsaccess.ts` (`readFile(path): Promise<Uint8Array | null>` — reads now, validates segments like `write`; null when missing; throws when unreadable), `IFolderAccess.ReadFileAsync(string path) → Task<byte[]?>`, `JsFolderAccess`, `FakeFolder`; tests via the shell logic in Task 4 (this task: FakeFolder behaviour + a small engine-level helper if needed).
- [ ] Commit `Client: read a file from the folder for viewing`.

### Task 3: Monaco vendored + viewer module

**Files:** `src/AiChromeProxy.Client/wwwroot/lib/monaco/vs/**` (from `https://registry.npmjs.org/monaco-editor/-/monaco-editor-<latest>.tgz`, `package/min/vs`, pruned: remove `ts.worker*`, `css.worker*`, `html.worker*`, `json.worker*`, language folders/files for language services, non-English `nls` files; keep loader, editor main, `editor.worker`, basic-languages, codicon font; verify the editor still loads), `LICENSE`/`ThirdPartyNotices.txt` from the package next to it, THIRD-PARTY-NOTICES.md entry; `Scripts/viewer.ts` (API in the spec; `require.config({ paths: { vs: '/lib/monaco/vs' } })` via the vendored `loader.js` injected as a classic script once; `MonacoEnvironment.getWorkerUrl` → `/lib/monaco/vs/...editor.worker...js`; `readOnly: true`, `domReadOnly: true`, `automaticLayout: true`, `minimap: { enabled: true }`, `scrollBeyondLastLine: false`; decorations for the revealed line and changed lines with CSS classes in app.css; theme `vs`/`vs-dark`); `Client/Navigator/IFileViewer.cs` + `JsFileViewer.cs`; `ClientPage` CSP additions + test; `BrowserScriptTests` allow-list (folder `wwwroot/lib/monaco/`); a minimal E2E smoke (open a file tab via a test hook or the tree → `.monaco-editor` present, no CSP violation).
- [ ] Commit `Client: vendored Monaco and a read-only viewer module`.

### Task 4: File tabs show the viewer

**Files:** `Shell/MainArea.razor` (file tab: slim details header + viewer host), new `Shell/FileView.razor` (loads via `ReadFileAsync` + `FileText.Decode`; states: loading, text → viewer, binary, too large, missing, unreadable error), `Navigator/ViewerPool.cs` (LRU ≤ 10 live instances, pure, tested), refresh: when `SyncEngine` reports a new hash for an open file (or the back channel wrote it) → re-read → `LineDiff.ChangedLines` → `update`; links: `#open=` handler opens the tab and calls `reveal(line|symbol)`; "Symbol not found" note; theme changes re-theme all live viewers; tests `FileViewLogicTests` (pure parts: state decisions, pool, refresh trigger dedupe) + E2E scenarios from the spec (tree → highlighted code; `path:3` → line 3 highlighted; `A.cs#Foo` → class Foo; folder change → new text + marks; binary file; dark/light screenshots to the scratchpad, looked at).
- [ ] Commit `Shell: file tabs open in the code viewer`.

### Task 5: Docs

**Files:** `docs/navigator.md` (what it does, limits, keyboard, links, manual checklist), README status line, CLAUDE.md (Client: Navigator, vendored Monaco), architecture follow-up line "Code navigator — done", `docs/chat.md` mention of `path#Symbol`.
- [ ] Commit `Docs: code navigator`.
