# Code navigator

Click a file in the tree (or a link in the chat) to read it in a read-only code viewer next to the chat. Design: [navigator spec](superpowers/specs/2026-10-04-code-navigator-design.md).

- The viewer is Monaco 0.57.0, vendored under `src/AiChromeProxy.Client/wwwroot/lib/monaco` (pruned to about 5.7 MB). Language services are off: it gives syntax highlighting only. `.razor` files use the cshtml/razor grammar.
- Files are read from the folder you opened in Chrome, not from the server, so the viewer works even when the server is unreachable.

## Links in the chat

- `path:line` opens the file at that line.
- `path#Symbol` opens the file at the first declaration of `Symbol`: the first line that has a declaration keyword (`class`, `interface`, `record`, `struct`, `enum`, `def`, `function`, `func`, `fn`, `void`, `public`, `private`, `protected`, `internal`, `static`, `const`, `let`, `var`, `type`) and the symbol as a whole word; otherwise the first whole-word match. No match shows "Symbol not found". A symbol is `[A-Za-z_][\w.]*`.

## Follows changes

The open file updates when it changes: your edits through the next scan, Claude's edits through the back channel. Changed lines are marked for 2 seconds. For files over 20 000 lines only the first and last differing lines are marked.

Up to 10 viewers stay alive at once; opening more releases the least recently used one (its tab reopens it).

## Limits

- Files over 5 MB: "Too large to show (n MB)".
- Binary files (a NUL in the first 8 KB, or invalid UTF-8): "Binary file — not shown". UTF-16 files with a BOM are decoded and shown.
- A file deleted from the folder: "This file is no longer in the folder".
- The viewer is read-only; edit files in your own editor.
- The page's CSP allows `worker-src 'self' blob:` for Monaco's workers; scripts still allow no `unsafe-inline`/`unsafe-eval`.

## Keyboard

| Keys | Action |
|---|---|
| Ctrl+F | Find in file |
| Ctrl+G | Go to line |
| Ctrl+Home / Ctrl+End | Start / end of file |

## Manual checklist

- [ ] Click a file in the tree: it opens in a tab with syntax highlighting; light and dark themes are readable.
- [ ] A `.razor`, `.ts`, `.json` and `.md` file each highlight sensibly.
- [ ] Ctrl+F, Ctrl+G and Ctrl+Home/End work; typing in the viewer changes nothing.
- [ ] Ask Claude to mention `path:line` and `path#Symbol`: each link opens the file at the right line; an unknown symbol shows "Symbol not found".
- [ ] Edit the open file in your editor: the viewer updates after the next scan and marks the changed lines for about 2 s. Let Claude edit it: the same via the back channel.
- [ ] Open a binary file (an image) and a file over 5 MB: the messages appear, nothing hangs.
- [ ] Delete the open file: "This file is no longer in the folder".
- [ ] Open 11+ files: all tabs still switch and show content.
- [ ] Turn the network off: already listed files still open.
- [ ] The browser console shows no CSP violations.
