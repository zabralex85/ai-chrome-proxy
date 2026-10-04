# Diagram tools — full screen, zoom, save

Date: 2026-10-04. Builds on [4 Claude chat](2026-10-04-claude-chat-design.md) (mermaid in chat, `Scripts/diagrams.ts`) and [tree actions](2026-10-04-tree-actions-design.md) (writing into the browser folder through `SyncEngine`). Owner's request: a class diagram in the chat should open full screen, zoom, and save "as an artifact" (the source, as a file in the repository) and as a picture — both downloaded and saved into the project folder.

## Goal

Every drawn diagram in the chat gets a small toolbar: **Open** (full-window viewer with zoom and pan), **Save source**, **Save SVG**, **Save PNG** (into the project folder, synced to the mirror like any file), **Download SVG**, **Download PNG**. The viewer has the same save and download actions.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Toolbar | `diagrams.ts` adds a toolbar (`role="toolbar"`, `aria-label="Diagram"`) above each drawn SVG: **Open**, **Save…** (menu: Source (.mmd), SVG, PNG — all into the project), **Download…** (menu: SVG, PNG). Icon + text buttons, visible on hover/focus within the diagram and always on touch (`@media (hover: none)`). Diagrams that failed to draw get only **Save source**. | One place for every action; quiet in the reading flow. |
| Viewer | An in-app overlay covering the whole window (`<dialog>` with `showModal()`: focus trap, Esc closes, focus returns to **Open**). The SVG is shown in the current theme, fitted to the window. Zoom: wheel (around the pointer), `+` / `-` keys and buttons, **Fit** (`0`), **100%** (`1`); range 10 %–800 %, step ×1.25. Pan: drag with the mouse/pen/touch, arrow keys (40 px). The zoom level is shown (e.g. `125%`). Transforms are CSS on a wrapper, so the SVG stays vector-sharp. | Large class diagrams need it; native dialog gives modality and a11y for free. |
| Save into the project | A save dialog (Blazor, `<dialog>`) with one path field: default `docs/diagrams/<name>.<ext>`; the extension follows the kind and is enforced (typing another one is replaced). `<name>`: the mermaid `title` (front matter `title:` or a `title` line) slugified; else the diagram type (first keyword: `classDiagram` → `class-diagram`, `flowchart`/`graph` → `flowchart`, `sequenceDiagram` → `sequence-diagram`, …) + `-` + local time `yyyyMMdd-HHmm`. Slug: lowercase ASCII letters/digits, other runs → `-`, trimmed, at most 60 chars, `diagram` if empty. Errors shown under the field, field stays: invalid path (`SyncPath.GetError` text), "'{name}' already exists here." (never overwrites: checked in the folder now), "Resolve the server change first", write refused. Missing folders are created. Success: an Actions-history entry "Saved 'path'." and a note in the dialog area "Saved to path" with an **Open** link for `.mmd`/`.svg` (opens the file tab). | Diagrams become part of the repo, where Claude and git see them. |
| How it is written | `SyncEngine.SaveFileAsync(path, Func<Task<byte[]>> content)` — the same gate as tree actions (write access asked from the click first, server-change refusal, the sync-cycle lock), then produces the bytes, then `IFolderAccess.WriteAsync`, then wakes the scan (the mirror gets it as an upload). Excluded paths are allowed with the note "Saved; excluded from sync". | Reuses the tested tree-action path; the bytes are made after the permission prompt so the click's activation is not spent on rendering. |
| SVG export | The drawn SVG's markup with `xmlns`, its `viewBox` size as `width`/`height`, and a background `<rect>` in the theme's background colour as the first child; UTF-8, `<?xml …?>` header. | Opens the same way everywhere. |
| PNG export | The diagram is drawn again for export with `htmlLabels: false` (mermaid's `foreignObject` labels would taint the canvas), loaded as `data:image/svg+xml;base64,…` into an `Image` (CSP `img-src 'self' data:` already allows it), drawn on a canvas at 2× scale (capped so the longer side ≤ 8192 px) over the background colour, `canvas.toBlob('image/png')`. | No CSP change; sharp on hi-dpi; bounded memory. |
| Theme of exports | The theme the user sees (dark or light), with its background. | What you see is what you save. |
| Download | Blob + `<a download="<name>.<ext>">` click, the same default name as the save dialog without the folder. | Works without folder access. |
| Without folder access / read-only browser | **Save…** items are disabled with the tooltip "Pick a folder first" when no folder is open; the write-access prompt handles read-only folders. Downloads always work. | Clear. |

## Testing

xunit (CI): default name (title from front matter and from a `title` line, each diagram type, slug rules: unicode → dashes, length cap, empty → `diagram`); extension enforcement; `SaveFileAsync` over `LoopbackServer` + `FakeFolder`: writes and uploads, refuses existing file, refuses while a server change waits, asks for write access and stops when refused, creates folders, excluded note, the content producer is called only after the gate.
E2E (Playwright, stubbed picker on OPFS, a FakeAgent script that answers with a class diagram): toolbar visible; **Open** shows the viewer, `+` zooms (level text changes), **Fit**, Esc closes and focus returns; **Save source** → dialog with `docs/diagrams/class-diagram-….mmd` → Save → file in the folder with the source and on the mirror after sync; saving again with the same name shows the "already exists" error; **Save PNG** → file starts with the PNG signature; **Download SVG** → a download named `….svg` whose content starts with `<?xml`; dark/light screenshots of the toolbar and the viewer to the scratchpad, looked at; no CSP violations.

## Known limitations

PNG labels use SVG text (no HTML formatting inside labels). Very large diagrams are scaled down to fit 8192 px. No copy-to-clipboard. No editing of diagrams.

## Done when

- Toolbar, viewer with zoom/pan, the three saves into the project and two downloads work from the chat and from the viewer.
- Saved files reach the mirror; existing files are never overwritten.
- CI green; gate ≥ 85 %; E2E green without CSP violations; docs updated (`docs/chat.md`).
