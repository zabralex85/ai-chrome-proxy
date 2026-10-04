# Sub-project 5 — Code navigator

Date: 2026-10-04. Parent: [architecture](2026-10-02-architecture-design.md) ("Code navigator — tree, Monaco (read-only), highlighting via `path:line` / `path#Symbol`"). Builds on [3a](2026-10-03-sync-and-shell-design.md) (file tree, file tabs, `fsaccess.ts`), [3b](2026-10-03-back-channel-design.md) (server edits reach the folder) and [4](2026-10-04-claude-chat-design.md) (chat links `path:line`).

## Goal

Clicking a file in the tree, or a `path:line` / `path#Symbol` link in the chat, opens the file in a read-only code viewer that looks and feels like VS Code: syntax highlighting, line numbers, find (Ctrl+F), go to line (Ctrl+G), minimap, folding, dark and light theme. The referenced line or symbol is revealed and highlighted. When the file changes (the user saves it, or Claude's edit arrives through the back channel), the open viewer updates and briefly marks the changed lines.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Editor | Monaco Editor (latest stable, MIT), **read-only**, the AMD `min/vs` build vendored under `src/AiChromeProxy.Client/wwwroot/lib/monaco/vs/` (downloaded from the npm tarball with curl; no Node). Pruned to what a viewer needs: the editor, the generic `editor.worker`, Monarch syntax for the basic languages; the language-service workers (TypeScript, CSS, HTML, JSON) and other locales are removed (target ≤ 15 MB). Version and licence recorded in THIRD-PARTY-NOTICES; `.gitattributes` keeps it byte-exact (`wwwroot/lib/** -text`, already there). | The architecture's choice; the AMD build loads with plain `<script>`/`require` from `'self'` (CSP-friendly) without a bundler. The ESM build would need Node. |
| Wrapper | `Scripts/viewer.ts` (strict TS): `open(element, path, text, language, line?, symbol?)`, `update(element, text)` (keeps scroll, marks changed lines for 2 s), `reveal(element, line?, symbol?)`, `setTheme(dark)`, `dispose(element)`. Language from the extension through Monaco's own language registry (fallback plain text). Theme follows the shell's `data-theme` (and the system theme when none is chosen), like the mermaid diagrams. Worker URL set through `MonacoEnvironment.getWorkerUrl` to the vendored `editor.worker` under `'self'`. | JS only where C# can't; one small typed seam (`IFileViewer`, thin `[ExcludeFromCodeCoverage]` C# wrapper). |
| CSP | Adds `worker-src 'self' blob:` (Monaco may wrap the worker in a blob) and keeps `script-src 'self' 'wasm-unsafe-eval' <hashes>`, `style-src 'self' 'unsafe-inline'`, `font-src 'self' data:` (Monaco's codicon font). E2E fails on any CSP violation (as in 4). | Monaco must run under the existing policy without `unsafe-eval`. |
| Content source | The browser reads the file from the picked folder (`fsaccess.ts` `readFile(path)` → bytes, read now, not from the last scan). Text when it decodes as UTF-8 (BOM stripped; UTF-16 with BOM decoded) and has no NUL in the first 8 KB; otherwise "Binary file — not shown". Larger than 5 MB → "Too large to show (n MB)". The file details (size, SHA-256, sync state) stay in a slim header above the viewer. | The folder is the user's truth and is local: instant, no server round trip, works while the connection is down; the 20 MB sync limit and the 5 MB view limit are separate on purpose (Monaco stays responsive). |
| Updates | When the sync engine's scan sees a new hash for an open file, or the back channel writes it, the viewer re-reads it and calls `update`; changed lines are found with a line diff in C# (`LineDiff`, pure, tested; ponytail: LCS over lines with a size cap — for files over 20 000 lines only the first and last differing lines are marked). A file deleted from the folder shows "This file is no longer in the folder" (as today). | Claude's edits become visible where the user is looking. |
| Links | `path:line` (existing) and `path#Symbol` (new: `[\w.]+` after `#`) from the chat open the file tab and reveal: a line → that line, centered and highlighted; a symbol → the first whole-word occurrence preceded by a declaration keyword (`class|interface|record|struct|enum|def|function|func|fn|void|public|private|protected|internal|static|const|let|var|type`) or, failing that, the first whole-word occurrence; not found → top of file and a status note "Symbol not found". The convention appended to Claude's system prompt adds `path#Symbol`. | The architecture's highlighting contract; cheap without a language server. |
| Tabs | A file tab now shows the viewer; tabs of files keep their scroll position while switching tabs (one Monaco instance per open file tab, disposed when the tab closes; at most 10 instances, the least recently used one is disposed and re-created when shown again). | VS Code-like tabs without unbounded memory. |
| Keyboard and a11y | Monaco's own keyboard model (Ctrl+F, Ctrl+G, Ctrl+Home/End) and its screen-reader mode; the tab panel keeps its `aria-labelledby`. | Built in. |

## Testing

xunit (CI): `LineDiff` (insert, delete, change, cap); text/binary/size decision (`FileText.Decode`: UTF-8, BOM, UTF-16 BOM, NUL, invalid UTF-8 → binary); symbol search (keywords, whole word, not found); link parsing for `path#Symbol` (validation like `path:line`: relative, no `..`); the open-file refresh logic in the shell (open tab + new hash → re-read once); CSP contains `worker-src`. The Monaco wrapper itself is checked by E2E.
E2E (local, Playwright): open a `.cs` file from the tree → Monaco shows highlighted code with line numbers; a chat answer with `src/A.cs:3` → the file opens with line 3 highlighted; `A.cs#Foo` → reveals `class Foo`; the file changes in the folder → the viewer shows the new text with changed lines marked; a binary file → "Binary file — not shown"; dark/light theme; no CSP violations.
Manual (`docs/sync.md` or a new `docs/navigator.md` section): open a large repo file, Ctrl+F, Ctrl+G, minimap; Claude edits a file you are viewing.

## Known limitations

Read-only (edit in your own editor or ask Claude); no cross-file search, go-to-definition or references (no language server); files over 5 MB and binary files are not shown; encodings other than UTF-8/UTF-16 show as binary.

## Done when

- Files open from the tree and from chat links in a read-only Monaco viewer with highlighting and the target line/symbol revealed.
- The viewer follows changes to the file.
- CI green; gate ≥ 85%; E2E green without CSP violations.

## Out of scope

Editing; workspace search; LSP features; diff view for conflicts (later, can reuse Monaco's diff editor); mobile app.
