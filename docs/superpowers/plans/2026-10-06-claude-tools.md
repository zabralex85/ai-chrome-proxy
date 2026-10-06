# Claude Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Project settings tab shows each MCP server and plugin of the home computer's Claude Code with its status and a per-project switch; switches apply to the next run through `--settings`.

**Spec:** [docs/superpowers/specs/2026-10-06-claude-tools-design.md](../specs/2026-10-06-claude-tools-design.md).

## Global Constraints

- **Safety:** never run the tray, Setup/Velopack, cloudflared, services or the real `claude` (tests use the FakeAgent); never touch `%ProgramData%\AiChromeProxy` (live install; port 5180 taken — local runs on a free port, temp mirror/database). Tests under `TempRootCleanup.Root`.
- Dependency direction unchanged (Domain ← Application ← Infrastructure ← Server; Client → Domain). Envelope changes additive only (camelCase public contract).
- Style: tabs; CRLF; UTF-8 without BOM (never write files through Python without `encoding='utf-8'`; prefer the Edit tool); no `this.`; `_camelCase`; sorted usings; file-scoped namespaces; one type per file (an enum may share); block-form `using (...) { }`; `Async` suffix; XML docs like the surrounding code; StyleCop errors fail the build. English only; example.com only.
- Exact values: settings keys `agentDisabledMcpServers`, `agentDisabledPlugins`, `agentApprovedMcpServers`; message types `agent.tools.get`, `agent.tools.check`, `agent.tools`; setting row key `agent.tools`; probe timeout 60 s each command; entry rules: trimmed, non-empty, no control chars, ≤ 200 chars, ≤ 100 entries; approval server name `aicp` never denied and hidden; statuses `connected`, `failed`, `needs-auth`, `not-configured`, `pending`, `off`, `missing`, `unknown`; `mcp list` marks `✔` connected, `✘` (or `✗`) failed with the text after `—` as the reason, `!` needs-auth, `-` not-configured, `⏸` pending; anything else unknown.
- Exact copy: heading **Claude tools**; lists **MCP servers**, **Plugins**; switch **On in this project**; button **Check now** / "Checking…"; "Last checked {time} (from a message)" / "(by Check now)"; badges Connected, Needs sign-in, Failed, Not configured, Waiting for approval, Off in this project, Not installed, Unknown; hints "Sign in on the home computer: run `claude`, then `/mcp`." and "Check the server on the home computer: `claude mcp get {name}`."; empty "No data yet — send a message or press Check now.".
- Commands: build `dotnet build -c Release` → 0/0; class `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<NS.Class>"`; gate `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`; E2E `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E`.
- Commit only the task's files; no AI attribution.

---

### Task 1: Contract, parsers, merge, arguments (pure)
**Files:** Domain: `ProjectSettings` (3 lists), `MessageTypes` (3 types), payloads `ClaudeToolsRequest { repo }`, `ClaudeToolsPayload`, row records, snapshot record. Application/Chat: `init` parsing in `StreamJsonParser` (a new event kind or a side output the run can read — choose the smallest change that keeps the chat event stream unchanged for the UI), `McpListParser`, `PluginListParser`, `ClaudeToolsMerge`. Infrastructure: `ClaudeArguments` `--settings` JSON. Tests for each (spec Testing list; sanitized recorded `mcp list` sample as a test fixture).
- [ ] Commit `Claude tools: settings, contract, parsers and the --settings argument`.

### Task 2: Snapshot, check and handlers (server)
**Files:** snapshot store (setting row `agent.tools` via the existing project store), `ChatService` saves the run's snapshot, Infrastructure probe runner (`<Agent:Command> mcp list`, `plugin list --json`, cwd = repo mirror, timeout, one per repo), Application handlers for `agent.tools.get` / `agent.tools.check` registered in `AddApplication`/`AddInfrastructure` like the others; FakeAgent answers `mcp list` and `plugin list --json` from env (`FAKE_AGENT_MCP_LIST`, `FAKE_AGENT_PLUGIN_LIST` file paths); tests incl. the probe with the FakeAgent and hub-level round trip.
- [ ] Commit `Claude tools: run snapshots, Check now and hub handlers`.

### Task 3: Project settings UI, E2E, docs
**Files:** Client: Project settings tab section (component per the spec), engine/state for `agent.tools`, save integration of the three lists, CSS; E2E scenarios from the spec + dark/light screenshots `claude-tools-*.png` in the scratchpad (looked at); `docs/chat.md` section + manual checklist items; README line.
- [ ] Commit `Shell: Claude tools in project settings`.
