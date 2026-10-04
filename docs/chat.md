# Claude chat

The chat tab runs Claude Code on the mirror of the folder you opened. Claude reads and edits the mirror; edits come back to your folder through the [back channel](sync.md#back-channel). Design: [Claude chat](superpowers/specs/2026-10-04-claude-chat-design.md).

## Setup

1. Install Claude Code on the home server so that `claude.exe` is on the `PATH` of the **service account** (the account the Windows service runs as; see [Windows host](windows-host.md)). `claude.exe` is preferred over `claude.cmd`. The native installer puts `claude.exe` in `%USERPROFILE%\.local\bin` and adds that folder to the **user** `PATH`, which the service may not see (a service gets the environment Windows had when the service manager started); set `Agent:Command` to the absolute path (e.g. `C:\Users\<you>\.local\bin\claude.exe`) or add the folder to the system `PATH` and restart.
2. Log in as that account: open a terminal as it and run `claude` once interactively. The service runs under your account, so the chat uses that account's `~/.claude`: your subscription login, plugins, MCP servers and `CLAUDE.md`.
3. Restart the service, open the app, open a folder and use the **Chat** tab.

## Agent options

Section `Agent` in `%ProgramData%\AiChromeProxy\appsettings.json` (or environment variables `Agent__Command`, ...):

```json
{
  "Agent": {
    "Command": "claude",
    "Args": [ "--max-turns", "30" ],
    "Env": { "EXAMPLE_VARIABLE": "value" },
    "IdleTimeout": "00:10:00"
  }
}
```

| Option | Default | Meaning |
|---|---|---|
| `Command` | `claude` | A bare name is looked up on `PATH` (`.exe`, then `.cmd`); an absolute path is used as is. |
| `Args` | none | Appended after the arguments the Server builds. |
| `Env` | none | Environment variables added to the process. |
| `IdleTimeout` | `00:10:00` | A run with no output for this long is killed. Must be positive, or `-00:00:00.001` for infinite. |

The Server runs `claude -p --output-format stream-json --verbose --include-partial-messages ...` with the prompt on stdin and the mirror repo folder as the working directory. Through a `.cmd` shim, allowed-tools rules containing `cmd` metacharacters are dropped, so those commands are asked about again.

## Permissions

**Project settings** tab, *Permissions*:

| Option | Claude Code mode |
|---|---|
| Ask before commands (edits are applied) — default | `acceptEdits` plus the approval tool |
| Allow everything | `bypassPermissions` |
| Only what my Claude Code settings allow | `dontAsk` |

*Model* (empty: Claude Code's default) is passed as `--model`.

## Approvals

In the default mode a command Claude wants to run shows a card with **Allow**, **Allow always in this project** and **Deny**. **Allow always** stores an exact rule for the project (`Bash(<command>)`, `PowerShell(<command>)`, the tool name for a tool without a command); commands containing `*`, line breaks, `:*` or parentheses that do not nest inside the rule (`a),Bash,(b` would turn into a rule allowing every command), and other tools that take a command, are allowed once only. A card not answered within 10 minutes is denied, as is any card pending at Stop or shutdown.

The card shows the whole command. A request too long to show in full (about 20 KB once encoded) shows its start and only **Deny**, with "The request is too long to show in full; it can only be denied."; the Server refuses to allow it too.

The approval tool is an MCP endpoint, `/mcp/approve`, reachable only from `127.0.0.1` with a per-run token.

### Reviewing and revoking always-allowed rules

**Project settings** → *Always allowed (one rule per line)* lists the project's rules (`agentAllowedTools`, passed to Claude as `--allowedTools`). Delete a line and **Save** to revoke a rule; you can also add rules in Claude Code's syntax (e.g. `Bash(npm test)`). The tab reads the settings again when it opens and right before saving, and a save replaces only the fields you changed, so a rule that a run added while the tab was open is kept unless you edited the list.

## Sessions and runs

- Sessions are stored in `aicp.db` and survive restarts and page reloads; a new message continues the session with `--resume`. Your messages appear in the history. **New chat** starts another session.
- A message may have up to 16 000 characters and about 30 KB once encoded: non-ASCII characters and quotes are sent as `\uXXXX` (6 bytes each), so a message in Cyrillic, for example, holds about 5 000 characters. A longer one is not sent: "The message is too long; shorten it."
- One run per repo at a time (a second send gets "busy"). **Stop** kills the run. A run continues if the browser disconnects; reopen the tab to see it. After the `result` event the process is killed if it does not exit within 5 seconds.
- Replies render as markdown (raw HTML is not rendered), mermaid diagrams (mermaid 12.1.0, vendored) and `path:line` and `path#Symbol` links that open the file's tab in the [code viewer](navigator.md). **Enter** sends, **Shift+Enter** adds a line.
- The status bar shows "Claude idle" or "Claude working... 0:42" and the last run's cost at API prices ("≈ $0.12 API"): on a Claude subscription runs are not charged, it only shows how heavy a run was.

## Diagrams

Each drawn diagram has a toolbar (shown on hover or focus, always on touch screens):

- **Open** shows it over the whole window: zoom with the wheel (around the pointer), `+` / `-`, **Fit** (`0`) and **100%** (`1`), from 10 % to 800 %; pan by dragging or with the arrow keys; **Esc** closes.
- **Save…** → **Source (.mmd)**, **SVG** or **PNG** writes a new file into the picked folder; it syncs to the mirror like any other file. The path defaults to `docs/diagrams/<name>.<ext>`: `<name>` is the diagram's `title` made file-safe, else its type and the local time (`class-diagram-20261004-1530`). Missing folders are created; an existing file is never overwritten ("'name' already exists here." — pick another name). The extension always matches the kind. The browser asks for write access the first time. A note "Saved to path" offers **Open** for `.mmd` and `.svg` files.
- **Download…** → **SVG** or **PNG** downloads the picture with the same name, also without a picked folder.

Pictures are saved in the theme you see, with its background. PNG is drawn at twice the size (at most 8192 px on the longer side) and uses plain SVG text for labels, so HTML formatting inside labels is lost. A diagram that failed to draw offers only its source.

## Security

- The hub accepts only the configured public host as `Origin` (no `Origin` passes, a wrong one gets 403; Development also allows localhost) and closes the connection when the Access token expires.
- The page ships a Content-Security-Policy; leave Cloudflare Rocket Loader, Zaraz and auto-injected analytics off, they break it.
- Claude can edit files and run commands as the service account: keep Cloudflare Access on.
- In *Ask before commands* mode edits are applied without a card, and the back channel writes them into your real folder (turn off *Apply server changes automatically* to review each one first).
- Approving a build or test command runs whatever build files and scripts Claude may just have edited (`Directory.Build.props`, `package.json` scripts, test code): approving `dotnet build` approves that code. `.git` is never synced in either direction, so hooks Claude writes under `.git/hooks` stay on the mirror, but a `git` command run there would run them.
- In `-p` mode Claude Code loads the repo's own `.claude/settings.json` (its hooks run without a card, its permissions apply), and Claude can write that file like any other edit, changing what the next run may do. Opt-in hardening: `"Args": [ "--setting-sources", "user" ]` in `Agent` loads only your user settings; the trade-off is that the project's `.claude/settings.json` and `.claude/settings.local.json` (their permissions, hooks and environment) are ignored.
- The run's approval token is in `--mcp-config` on Claude's command line, so other processes of the same account can read it; with it they can only create approval cards, not answer them.

## Limitations

- No attachments, no slash commands or TUI.
- One run per repo.
- The cost is Claude Code's own estimate at API prices, not what a subscription is charged.
- Edit conflicts behave as in the [back channel](sync.md#back-channel).

## Manual checklist

Run this before a release that touches the chat. Use a test repository.

- [ ] As the service account, `claude` is on `PATH` and logged in (`claude -p "hi"` answers).
- [ ] Send a message: text streams in, status shows "Claude working..." and then "Claude idle" with a cost.
- [ ] Ask for a mermaid diagram: it renders; `path:line` and `path#Symbol` links open the file's tab.
- [ ] On a diagram: **Open** → zoom, pan, **Fit**, **Esc**; **Save…** → **Source**, **SVG**, **PNG** land in `docs/diagrams/` in your folder and on the mirror; saving the same name again is refused; **Download…** → SVG and PNG open in an image viewer and look like the screen.
- [ ] Ask for a file edit: the mirror changes and the file in your folder follows within ~5 s.
- [ ] Ask to run a command: the card appears; **Deny** makes Claude report the denial; **Allow** runs it; **Allow always in this project** skips the card next time (and not for a command with `*`).
- [ ] Switch to *Allow everything* and *Only what my Claude Code settings allow*: behavior matches.
- [ ] **Stop** ends a long run at once; sending during a run says busy.
- [ ] Reload the page mid-run: the history and the running run reappear; restart the service and the history is still there, and a new message continues the session.
- [ ] Leave a card unanswered 10 minutes: it is denied.
- [ ] In *Ask before commands* mode, ask Claude to write `.claude/settings.json`: does it ask? (If not, consider `--setting-sources user`, see Security.)
- [ ] `curl http://127.0.0.1:<port>/mcp/approve` without the token: 404. From another machine the port does not answer (the Server listens on loopback only: connection refused); through the tunnel, Cloudflare Access asks for a login first.
- [ ] *Always allowed* in the Project settings lists a rule added with **Allow always**; deleting it and saving brings the card back next time.
- [ ] Open the app from a page on another origin: the hub refuses it (403).
