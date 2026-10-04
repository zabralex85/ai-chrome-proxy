# Claude chat

The chat tab runs Claude Code on the mirror of the folder you opened. Claude reads and edits the mirror; edits come back to your folder through the [back channel](sync.md#back-channel). Design: [Claude chat](superpowers/specs/2026-10-04-claude-chat-design.md).

## Setup

1. Install Claude Code on the home server so that `claude.exe` is on the `PATH` of the **service account** (the account the Windows service runs as; see [Windows host](windows-host.md)). `claude.exe` is preferred over `claude.cmd`.
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

In the default mode a command Claude wants to run shows a card with **Allow**, **Allow always in this project** and **Deny**. **Allow always** stores an exact rule for the project (`Bash(<command>)`, `PowerShell(<command>)`, the tool name otherwise); commands containing `*`, line breaks or `:*` are allowed once only. A card not answered within 10 minutes is denied, as is any card pending at Stop or shutdown.

The approval tool is an MCP endpoint, `/mcp/approve`, reachable only from `127.0.0.1` with a per-run token.

## Sessions and runs

- Sessions are stored in `aicp.db` and survive restarts and page reloads; a new message continues the session with `--resume`. Your messages appear in the history. **New chat** starts another session.
- One run per repo at a time (a second send gets "busy"). **Stop** kills the run. A run continues if the browser disconnects; reopen the tab to see it. After the `result` event the process is killed if it does not exit within 5 seconds.
- Replies render as markdown (raw HTML is not rendered), mermaid diagrams (mermaid 12.1.0, vendored) and `path:line` links that open the file's tab. **Enter** sends, **Shift+Enter** adds a line.
- The status bar shows "Claude idle" or "Claude working... 0:42" and the last run's cost.

## Security

- The hub accepts only the configured public host as `Origin` (no `Origin` passes, a wrong one gets 403; Development also allows localhost) and closes the connection when the Access token expires.
- The page ships a Content-Security-Policy; leave Cloudflare Rocket Loader, Zaraz and auto-injected analytics off, they break it.
- Claude can edit files and run commands as the service account: keep Cloudflare Access on.

## Limitations

- No attachments, no slash commands or TUI.
- One run per repo.
- The cost is Claude Code's own report (API-equivalent for subscriptions).
- Edit conflicts behave as in the [back channel](sync.md#back-channel).

## Manual checklist

Run this before a release that touches the chat. Use a test repository.

- [ ] As the service account, `claude` is on `PATH` and logged in (`claude -p "hi"` answers).
- [ ] Send a message: text streams in, status shows "Claude working..." and then "Claude idle" with a cost.
- [ ] Ask for a mermaid diagram: it renders; a `path:line` link opens the file's tab.
- [ ] Ask for a file edit: the mirror changes and the file in your folder follows within ~5 s.
- [ ] Ask to run a command: the card appears; **Deny** makes Claude report the denial; **Allow** runs it; **Allow always in this project** skips the card next time (and not for a command with `*`).
- [ ] Switch to *Allow everything* and *Only what my Claude Code settings allow*: behavior matches.
- [ ] **Stop** ends a long run at once; sending during a run says busy.
- [ ] Reload the page mid-run: the history and the running run reappear; restart the service and the history is still there, and a new message continues the session.
- [ ] Leave a card unanswered 10 minutes: it is denied.
- [ ] `curl http://127.0.0.1:<port>/mcp/approve` without the token and from another machine: refused (404).
- [ ] Open the app from a page on another origin: the hub refuses it (403).
