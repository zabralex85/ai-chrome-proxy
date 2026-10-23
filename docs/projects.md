# Server projects

Every folder synced to the server is a project there, whichever computer synced it. The folder name in the top bar opens **Projects on the server**: each project with "this folder" (the one open in this browser), "Claude is working", "online" (a browser has the folder open now) and when its chat was last active ("no chat yet" when it has none). Keyboard: arrows, Home and End move, Enter or Space picks, Esc or Tab closes. Picking the project already shown does nothing. Design: [server projects spec](superpowers/specs/2026-10-23-server-projects-design.md).

Picking another project shows it **read-only**: its chat (history and live answers), its file tree and its files, all read from the server. The top bar shows a **Read-only** pill and **Back to my folder**, which returns to the folder open in this browser, with its tabs and chat. The folder keeps syncing meanwhile; its attention state (access needed, unsupported browser, N errors; not conflicts) shows on the **Back** button. The Explorer shows the server's tree with a **Refresh** button, without sync state, context menu, sync status or conflicts. Chat links (`path:line`, `path#Symbol`) open the viewed project's files; the file bar shows the size and "On the server".

Nothing in the view changes the other project: sending, **Stop**, permission answers, attachments (including paste and drop into the input), **New chat**, tree actions and **Project settings** are disabled. The chat input placeholder and the **New chat** tooltip say "Read-only: open this folder on its computer to chat."; the **Project settings** tooltip says "Read-only"; **Stop**, the attach control and the permission buttons are disabled without a read-only tooltip. The chat stays read-only after **Back to my folder** until the local folder's own chat has opened again (`ChatEngine.IsReadOnlyLocked`), and the Claude tools are refused too. To work in that project, open its folder on its computer.

## Protocol

| Request → reply | Payload |
|---|---|
| `repos.list` → `repos` | `{}` → `{repos: [{name, lastActivity, running, open}], truncated}` |
| `mirror.list` → `mirror.files` | `{repo, after}` → `{repo, files: [{path, size, modified}], next}` (pages of at most 500 entries / 24 000 bytes) |
| `mirror.read` → `mirror.data` | `{repo, path, offset}` → `{repo, path, offset, data, last, sha256}` (16 KB chunks, base64url) |

Excluded paths (the mirror's `.gitignore` and the project's extra excludes) are neither listed nor served; a chat link to one shows "Could not read the file: '<path>' is excluded from sync." in the tab. None of the three writes anything.

## Refresh

While a project is viewed, `mirror.list` runs again on four triggers: every 30 s, only while the tab is visible and connected (`MirrorBrowser`); once when a run of the viewed project ends (`RunEndWatch`, once per ended run); after a reconnect; and on **Refresh**. The last three do not depend on the tab being visible. An open file is read again when its size or time changed; its tab shows "This file is no longer on the server" when it is gone.

## Known limitations

- While another project is viewed, the open folder's chat gets no live pushes (one chat subscription per connection); its runs go on and the history catches up on return.
- Folders with the same name on two computers share one mirror and one chat.
- Empty folders of the mirror are not shown.
- Files Claude creates in the other project appear within 30 s (while the tab is visible), when its run ends, or at once with **Refresh**.

## Manual checklist

- [ ] Sync folder A on computer 1 and folder B on computer 2. On computer 1 the menu lists A ("this folder") and B ("online"); close B's tab on computer 2: B loses "online" on the next open of the menu.
- [ ] Pick B: the read-only pill and **Back to my folder** show; the tree is B's; a file opens with highlighting; the chat input, **Stop**, **New chat** and **Project settings** are disabled.
- [ ] Start a run in B on computer 2: computer 1 sees "Claude is working" in the menu and the live answer in B's chat; when it ends, the files Claude changed refresh in the tree and an open file marks its changed lines.
- [ ] A chat link `path:line` in B's chat opens B's file from the server.
- [ ] **Back to my folder**: A's tree, tabs and chat return; a run that went on in A meanwhile shows in full.
- [ ] Delete B's mirror folder on the server while viewing it: **Refresh** shows "This project is no longer on the server".
- [ ] Disconnect while viewing: the projects button is disabled ("Not connected"); after reconnect the view's chat and files load again.
