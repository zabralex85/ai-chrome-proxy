# Claude tools — see and switch MCP servers and plugins per project

Date: 2026-10-06. Builds on [4 Claude chat](2026-10-04-claude-chat-design.md) (runs, `ClaudeArguments`, `StreamJsonParser`) and [3b project settings](2026-10-03-back-channel-design.md) (`ProjectSettings`, the Project settings tab). Owner's request: the project has settings, but we cannot tell which plugins and MCP servers work or not; we need to manage them per project.

## What Claude Code offers (verified on 2.1.289)

- Each run's `system`/`init` event lists `mcp_servers: [{ name, status, source }]` (`status`: `connected`, `failed`, `needs-auth`, `pending`; `source`: `user`, `project`, `local`, `plugin`, `claudeai`; plugin servers are named `plugin:<plugin>:<server>`) and `plugins: [{ name, source, version?, path }]` (`source` is the plugin id `name@marketplace`).
- `claude mcp list` health-checks the configured servers without a model call; one line per server: `<name>: <command or url> - <mark> <status text>` with `✔ Connected`, `✘ Failed to connect — <reason>`, `! Needs authentication`, `- Not configured`, `⏸ Pending approval` (from the help text; not seen in a recording) (other lines — progress, warnings — are noise).
- `claude plugin list --json` lists installed plugins: `[{ id, version, scope, enabled, ... }]` without a model call.
- Per run, `--settings '<json>'` adds settings on top of the user's: `{"enabledPlugins": {"<id>": false}}` turns a plugin (and its MCP servers) off, `{"deniedMcpServers": [{"serverName": "<name>"}]}` turns an MCP server off, `{"enabledMcpjsonServers": ["<name>"]}` approves a project `.mcp.json` server. Nothing in Claude's own files changes.

## Goal

**Project settings** gets a **Claude tools** section: every MCP server and plugin the home computer's Claude Code has, with its last known status and an **On in this project** switch. **Check now** refreshes statuses without spending a message. Switches apply from the next message (and are shown in the section as "Off in this project").

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Settings | `ProjectSettings` gains `AgentDisabledMcpServers` (names), `AgentDisabledPlugins` (ids), `AgentApprovedMcpServers` (names of project `.mcp.json` servers to approve). Compatible additions (camelCase JSON). | Per project, in the existing store and save flow. |
| Applying | `ClaudeArguments` adds one `--settings <json>` when any list is non-empty: `enabledPlugins` (each disabled id → false), `deniedMcpServers` (each disabled name), `enabledMcpjsonServers` (approved names). The approval server `aicp` is never denied (filtered). Names/ids: trimmed, non-empty, no control characters, ≤ 200 chars, at most 100 entries each (others dropped). | Native mechanism; nothing written to `~/.claude*`. |
| Status sources | (1) **Last run**: `StreamJsonParser` reads `init` → `ChatService` saves a snapshot for the repo (servers, plugins, time, `from: "run"`). (2) **Check now**: the server runs `<Agent:Command> mcp list` and `<Agent:Command> plugin list --json` in the repo's mirror folder (same environment and account as runs), 60 s timeout each, both results → snapshot `from: "check"`. A failed or timed-out check keeps the old snapshot and reports the error. One check per repo at a time. | Free status from every message; a cheap explicit refresh. |
| Snapshot storage | `aicp.db` setting row per repo (`agent.tools`, JSON), newest wins. | No schema change. |
| Rows | Pure merge (Application) of snapshot + settings → `ClaudeToolsPayload { servers: [{ name, source, status, onHere, plugin? }], plugins: [{ id, name, version, enabled, onHere }], checkedAt, from, error? }`. Servers from plugins carry `plugin` and no own switch (the plugin's switch covers them). A server or plugin that is off in this project shows status `off` when the snapshot came from a run (Claude did not load it), else its checked status. Disabled entries that are no longer installed still show (so they can be switched back on) with status `missing`. `aicp` is hidden. Sorted: problems first (`failed`, `needs-auth`, `pending`), then by name. | One place decides what the UI shows; tested. |
| Messages | `agent.tools.get` (client → server `{ repo }`) and `agent.tools.check` (`{ repo }`) both reply `agent.tools` (`ClaudeToolsPayload` + `repo`). Additive Envelope types. | Same pattern as `project.settings`. |
| UI | In the Project settings tab under the agent settings: heading **Claude tools**, line "Last checked {time} ({from a message | by Check now})" + **Check now** (busy: "Checking…"). Two lists **MCP servers** and **Plugins**; each row: name, small source text (`user`, `project`, `plugin X`, `claude.ai`), status badge (text + colour: Connected / Needs sign-in / Failed / Waiting for approval / Off in this project / Not installed), switch **On in this project** (a pending project server's switch means "approve"). Hints under problem rows: needs sign-in → "Sign in on the home computer: run `claude`, then `/mcp`."; failed → "Check the server on the home computer: `claude mcp get <name>`." Switch changes go through the existing **Save** of the tab. Empty snapshot: "No data yet — send a message or press Check now." | Matches the existing settings form. |
| Limits | Statuses are those of the home computer's Claude Code account; a check starts the MCP servers like a run would. Plugin-provided skills, agents and slash commands are not listed. | Keep it focused. |

## Testing

xunit: init parsing (servers, plugins, unknown fields, missing lists); `mcp list` parser on a recorded, sanitized output (noise lines, names with `:` and spaces, each mark, unknown mark → `unknown`); `plugin list --json` parser (fields, bad JSON → error); `--settings` JSON from settings (each list, `aicp` filtered, limits, none → no flag; argument placed so variadic flags are not broken); merge table (each status, off-here from run vs check, missing entries, plugin servers grouped, sort); handler get/check (busy, timeout keeps old snapshot, error text); `ChatService` saves the snapshot from a run. FakeAgent learns `mcp list` / `plugin list --json` answers from env; probe runner tested with it. E2E: settings tab shows servers and plugins from the fake, **Check now** refreshes, switching a server off and saving → the next run's arguments (`FAKE_AGENT_ARGS_FILE`) contain the `--settings` with `deniedMcpServers`; screenshots dark/light, looked at.

## Done when

- The Claude tools section shows real statuses after a message and after **Check now**; switches change what the next run loads.
- CI green; gate ≥ 85 %; E2E green; `docs/chat.md` explains it; manual check on the real install (owner's servers and plugins show; switching `blender` off removes it from the next run).
