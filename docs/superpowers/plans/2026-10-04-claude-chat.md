# Claude Chat (4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chat with Claude Code running on the home server in the repo's mirror: streamed markdown answers with mermaid diagrams, tool rows, approvals in the chat, persistent sessions; plus the two security follow-ups (Origin allow-list, token expiry) that must land before an agent runs.

**Architecture:** Domain gets the chat wire contract. Application gets the pure `StreamJsonParser`, the `ChatService` (one run per repo, off the hub invocation, subscribers, cancel, idle timeout), the `PermissionBroker` and the ports `IAgentRunner`, `IChatStore`. Infrastructure gets `ClaudeRunner` (argument building is pure and tested; the process part is a thin `[ExcludeFromCodeCoverage]` class reusing the Job-object pattern of `CloudflaredProcess`), `AgentOptions` and the chat tables in `aicp.db`. The Server hosts the loopback-only MCP approval endpoint and the hub hardening. The Client gets a `ChatEngine` and the Chat tab (Markdig in C#, mermaid via a vendored script and `Scripts/diagrams.ts`).

**Tech Stack:** .NET 10, SignalR, Blazor WebAssembly, SQLite (`Microsoft.Data.Sqlite`, existing), `ModelContextProtocol.AspNetCore` (new, Server only, latest stable 2.x), `Markdig` (new, Client, latest stable), mermaid (vendored `mermaid.min.js`, latest 12.x, MIT), xunit v3, Reqnroll + Playwright.

**Spec:** [docs/superpowers/specs/2026-10-04-claude-chat-design.md](../specs/2026-10-04-claude-chat-design.md).

## Global Constraints

- **Safety while implementing:** never run the tray, Setup/Velopack, cloudflared, real services or UAC; never touch the real `%ProgramData%\AiChromeProxy` or `%LocalAppData%\AiChromeProxy` (a live install runs here; port 5180 is taken — local runs use e.g. `Server__Port=5197` with temp `Mirror__Root` / `Projects__Database`); **never run the real `claude` CLI from tests or scripts** (use a fake agent command). Tests use temp folders under `TempRootCleanup.Root`.
- Dependency direction enforced by `tests/AiChromeProxy.Tests/Architecture` (must stay green unchanged): Domain (BCL only) ← Application ← Infrastructure ← Server; Client → Domain only. `ModelContextProtocol.AspNetCore` only in Server; `Markdig` only in Client.
- Style: tabs; CRLF; UTF-8 without BOM; no `this.`; `_camelCase` fields; sorted usings; file-scoped namespaces; one type per file (an enum may share); block-form `using (...) { }` only; `Async` suffix; XML docs like the surrounding code; StyleCop errors fail the build — fix code, never the ruleset. English only; no personal paths or domains (example.com).
- TypeScript strict, no `any`, no hand-written `.js` except the vendored third-party `wwwroot/lib/mermaid/mermaid.min.js` (`BrowserScriptTests` must allow exactly that vendored path, nothing else); interop wrappers `[ExcludeFromCodeCoverage]` without logic.
- `Envelope` JSON is a public camelCase contract: only new message types, new optional fields, new error codes.
- Exact values from the spec:
  - Runner args: `-p --output-format stream-json --verbose --include-partial-messages [--resume <id>] --permission-mode <m> [--permission-prompts host --permission-prompt-tool mcp__aicp__approve --mcp-config <json>] [--model <m>] [--allowedTools <rules…>] --append-system-prompt <convention>`; prompt on stdin; cwd `<mirror>\<repo>`; `Agent:Command` default `claude` (PATH: `claude.exe`, then `claude.cmd`), `Agent:Args`, `Agent:Env`, `Agent:IdleTimeout` default 10 min.
  - Permission modes (setting `agentPermissions`): `ask` (default) → `acceptEdits` + approval tool; `all` → `bypassPermissions`; `settings` → `dontAsk`, no approval tool. `agentAllowedTools` (list of rules; **Allow always** adds `Bash(<command>)` for shell commands, the tool name otherwise). `agentModel` (empty = default).
  - Convention: "You are used through a web UI. Draw diagrams as ```mermaid fenced blocks. Refer to code as `path:line` relative to the repository root."
  - Messages: `chat.open {repo}` → `chat.sessions {repo, sessions[{id, title, updated, running}]}`; `chat.history {sessionId, afterSeq}` → `chat.events {sessionId, events[], final}`; `chat.send {repo, sessionId?, text}` (≤ 16 000 chars) → `chat.started {sessionId, runId}`; `chat.cancel {runId}` → echo; `chat.approve {runId, requestId, decision}` (`allow | allowAlways | deny`) → echo; push `chat.event {sessionId, runId, seq, kind, …}` with kinds `text`, `message`, `tool`, `toolResult`, `permission`, `permissionResolved`, `result`; error code `busy`. Events and pages ≤ 24 000 bytes serialized; summaries truncated with "…"; tool result summary = first 2 KB.
  - Approval: MCP Streamable HTTP at `/mcp/approve`, loopback only + per-run bearer token, else 404; tool `approve {tool_name, input}` → `{"behavior":"allow","updatedInput":input}` / `{"behavior":"deny","message":"Denied in the chat."}`; no answer in 10 min, cancel or shutdown → deny.
  - Origin: a `/hub` request with an `Origin` other than `https://<Server:PublicHost>` (Development also `http://localhost:*`, `http://127.0.0.1:*`) → 403; no `Origin` → passes (Access still required). Token `exp` → connection aborted when it passes.
  - UI copy: **Enter** sends, **Shift+Enter** new line; **Stop**; **New chat**; **Allow**, **Allow always in this project**, **Deny**; status 2 "Claude idle" / "Claude working… 0:42", last run's cost "$0.12"; settings: "Permissions" radios "Ask before commands (edits are applied)", "Allow everything", "Only what my Claude Code settings allow"; "Model" input (placeholder "Claude Code's default").
- Commands (repo root): build `dotnet build -c Release` → 0 warnings / 0 errors; one class `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<Namespace.Class>"`; gate `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`; E2E `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E`.
- Commit only the task's files; no AI attribution.

## Decisions taken while planning

1. **Chat state lives next to the project store.** `IChatStore` is a separate Application port; `SqliteChatStore` uses the same `aicp.db` file (`ProjectsOptions.Database`) and adds its tables with `CREATE TABLE IF NOT EXISTS` in its own transaction (schema version bump to 2 in `SqliteProjectStore`'s migration so both agree: version 1 → 2 creates the chat tables).
2. **Subscribers.** `ChatService` keeps `repo → set of (connectionId, send)`; `chat.open` subscribes, the hub's disconnect unsubscribes (wire into `TransportHub.OnDisconnectedAsync` next to `SyncSessions.Close`).
3. **Session ↔ Claude session.** `chat_session.claude_session_id` is set from the first run's `system/init`; later runs pass `--resume`. A run whose resume fails (Claude reports an unknown session) ends with its error; no silent new session.
4. **Approval request ids** are GUIDs (N format); a pending request is keyed by (runId, requestId).
5. **The runner contract:** `IAgentRunner.StartAsync(AgentRun run, CancellationToken ct)` returns an `IAgentProcess` with `IAsyncEnumerable<string> Lines`, `Task<int> Exited`, `Kill()`. `ChatService` reads lines, feeds the parser, stores and pushes events, enforces the idle timeout and cancel.
6. **Fake agent for E2E and one process test:** a small console project `tests/AiChromeProxy.FakeAgent` (net10.0) that ignores its args, reads stdin and prints a fixture file's lines with small delays; for the approval scenario it calls the MCP endpoint given in `--mcp-config` (so the whole approval path is exercised without `claude`).
7. **Hub hardening is Task 1** (independent, small) so it lands first.

---

### Task 1: Hub hardening — Origin allow-list and token expiry

**Files:** `src/AiChromeProxy.Server/Program.cs`, `src/AiChromeProxy.Server/Security/CloudflareAccessMiddleware.cs` (store `exp` in `context.Items`), new `src/AiChromeProxy.Server/Security/HubOriginFilter.cs` (middleware for `/hub*`), `src/AiChromeProxy.Server/Transport/TransportHub.cs` (on connect: schedule `Context.Abort()` at `exp` via `TimeProvider`), tests `tests/AiChromeProxy.Tests/Server/HubOriginFilterTests.cs`, `TransportHubTests.cs` (expiry).

- [ ] Tests first: Origin `https://code.example.com` with `PublicHost=code.example.com` → 101/200 negotiate; missing Origin → allowed (Access still required); `https://evil.example.com`, `http://code.example.com` → 403; Development: `http://localhost:5197` allowed. Expiry: a connection whose token `exp` is 2 s ahead (FakeTimeProvider) is aborted after advancing 2 s; a connection without Access (Development) is never aborted.
- [ ] Implement; the client's existing reconnect handles an aborted connection (it reconnects through Access).
- [ ] Build, run Server test classes + full gate; commit `Hub: Origin allow-list and abort at token expiry`.

### Task 2: Chat wire contract (Domain)

**Files:** `src/AiChromeProxy.Domain/Chat/*.cs` (records: `ChatOpenPayload(Repo)`, `ChatSessionInfo(Id, Title, Updated, Running)`, `ChatSessionsPayload(Repo, Sessions)`, `ChatHistoryPayload(SessionId, AfterSeq)`, `ChatEventsPayload(SessionId, Events, Final)`, `ChatSendPayload(Repo, SessionId?, Text)`, `ChatStartedPayload(SessionId, RunId)`, `ChatCancelPayload(RunId)`, `ChatApprovePayload(RunId, RequestId, Decision)`, `ChatEvent(SessionId, RunId, Seq, Kind, Text?, ToolId?, Name?, Summary?, IsError?, RequestId?, Decision?, Ok?, CostUsd?, DurationMs?, Error?)` as one record with optional fields, `ChatEventKinds` constants, `ChatLimits` (MaxTextChars 16 000, MaxEventBytes 24 000, ToolSummaryBytes 2 048), `ChatEventSplitter.Split(ChatEvent) → IReadOnlyList<ChatEvent>` (splits long `text`/`message` text on UTF-16 boundaries without breaking surrogate pairs, truncates summaries with "…")), `MessageTypes` (+10 constants), `ErrorCodes.Busy = "busy"`; `ProjectSettings` gains `AgentPermissions`, `AgentModel`, `AgentAllowedTools` (strings/list, optional) with `AgentPermissionsOrDefault` ("ask").
- [ ] Tests: JSON shape (camelCase, nulls omitted? — keep `JsonSerializerOptions.Web` defaults like 3b, assert the exact JSON of one event of each kind), splitter (a 100 000-char message → pages each ≤ 24 000 bytes serialized, concatenation equals the input, emoji not split), settings round trip with the new keys and unknown keys kept.
- [ ] Commit `Domain: chat contract`.

### Task 3: Stream-json parser (Application)

**Files:** `src/AiChromeProxy.Application/Chat/StreamJsonParser.cs` (stateful per run: `IReadOnlyList<ParsedEvent> Feed(string line)`; exposes `ClaudeSessionId`), `ParsedEvent` (kind + fields, no session/seq), fixtures `tests/AiChromeProxy.Tests/Application/Fixtures/stream-json/*.jsonl` (text-answer, tool-use, error-result, partial-messages, unknown-and-malformed), tests `StreamJsonParserTests`.
- [ ] Write the fixtures by hand from the documented Claude Code stream-json shapes (`{"type":"system","subtype":"init","session_id":…}`, `{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":…}}}`, `{"type":"assistant","message":{"content":[{"type":"text","text":…},{"type":"tool_use","id":…,"name":…,"input":{…}}]}}`, `{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":…,"content":…,"is_error":…}]}}`, `{"type":"result","subtype":"success"|"error_during_execution","is_error":…,"result":…,"total_cost_usd":…,"duration_ms":…,"num_turns":…,"session_id":…}`); tool summary: `Bash` → the command, `Edit`/`Write`/`Read` → the file path, others → compact JSON of the input; tool result content as string or array of text blocks.
- [ ] Tests per fixture (exact parsed sequences); malformed line → no event, parser continues.
- [ ] Commit `Chat: stream-json parser`.

### Task 4: Chat store (SQLite)

**Files:** `src/AiChromeProxy.Application/Chat/IChatStore.cs` (`CreateSession(repo, title) → ChatSessionInfo`, `ListSessions(repo)`, `SetClaudeSession(id, claudeId)`, `GetClaudeSession(id)`, `Touch(id)`, `Append(sessionId, IReadOnlyList<ChatEvent>)` (assigns `seq`, returns stored events), `Read(sessionId, afterSeq, maxBytes) → (events, final)`, `SessionRepo(id)`), `src/AiChromeProxy.Infrastructure/Chat/SqliteChatStore.cs`, schema v2 migration in `SqliteProjectStore` (Decision 1), `tests/AiChromeProxy.Tests/Application/MemoryChatStore.cs`, tests `SqliteChatStoreTests` (v1 database upgrades to v2 keeping bases/settings; sessions ordered by `updated` desc; `seq` monotonic per session; paging by bytes; `text` kind refused by `Append` — the service never stores deltas).
- [ ] Commit `Chat: sessions and events in aicp.db`.

### Task 5: Agent runner (Infrastructure)

**Files:** `src/AiChromeProxy.Application/Chat/IAgentRunner.cs`, `AgentRun.cs` (repo folder, prompt, resume id, permissions mode, model, allowed tools, approval endpoint url + token or null), `IAgentProcess.cs`; `src/AiChromeProxy.Infrastructure/Chat/AgentOptions.cs` (`Command`, `Args`, `Env`, `IdleTimeout`), `ClaudeArguments.cs` (pure: `Build(AgentRun, AgentOptions) → IReadOnlyList<string>` and `ResolveCommand(command, pathDirs, fileExists)`), `ClaudeRunner.cs` + `AgentProcess.cs` (`[ExcludeFromCodeCoverage]`, Job object kill-on-close like `CloudflaredProcess`, stdin write + close, stdout lines, stderr captured to the log and, on a non-zero exit with no `result` line, turned into the error text); DI in `AddInfrastructure` (section `Agent`).
- [ ] Tests: argument lists per mode (`ask` with MCP config JSON containing the URL and `Authorization: Bearer <token>` header, `all`, `settings`), resume/model/allowed tools present only when set, `Agent:Args` appended last; `ResolveCommand` (`claude` → `claude.exe` found first, else `claude.cmd`, absolute path kept, not found → null). One process test runs `tests/AiChromeProxy.FakeAgent` (Decision 6; create the project here) and reads its lines.
- [ ] Commit `Chat: Claude runner`.

### Task 6: Chat service and handlers (Application)

**Files:** `src/AiChromeProxy.Application/Chat/ChatService.cs` (runs, subscribers, push, store, idle timeout via `TimeProvider`, cancel, busy, application-stopping cancellation), `ChatHandler.cs` (types `chat.open`, `chat.history`, `chat.send`, `chat.cancel`, `chat.approve` → service), DI in `AddApplication`, `TransportHub.OnDisconnectedAsync` unsubscribe, `LoopbackServer` registration; tests `ChatServiceTests` with a fake `IAgentRunner` (scripted lines, controllable exit) and `MemoryChatStore`.
- [ ] The service finds the repo folder through a new `IMirrorStore.RepoFolder(repo)` (absolute `<root>\<repo>`, null when it does not exist); add it to `FileSystemMirrorStore` with a test.
- [ ] Tests: send → `chat.started` at once, events pushed to all subscribers of the repo and stored (no `text` stored, `message` stored), `result` ends the run and `chat.sessions` shows `running:false`; second send while running → `busy`; cancel → process killed, `result {ok:false, error:"Cancelled."}`; idle 10 min (fake clock) → killed with an idle error; runner start failure (command not found) → `result {ok:false, error:"`claude` was not found on PATH for the service account; set Agent:Command."}`; resume id stored from init and passed next turn; history paging; disconnect of the sender does not stop the run; repo name sanitized (`RepoName.Sanitize`), unknown session → `not_found`, text > 16 000 → `too_large`; mirror folder missing → `bad_request` ("Open the folder and let it sync first.").
- [ ] Commit `Chat: run Claude per message, stream events to subscribers`.

### Task 7: Approvals — broker and MCP endpoint

**Files:** `src/AiChromeProxy.Application/Chat/PermissionBroker.cs` (per run: token, pending requests, `RequestAsync(runId, tool, input, ct) → decision`, `Answer(runId, requestId, decision)`, allow-always rule building, `CancelRun(runId)`), wiring into `ChatService` (push `permission` / `permissionResolved`, add the rule to `agentAllowedTools` via `IProjectStore`), `src/AiChromeProxy.Server/Chat/ApprovalMcp.cs` (MCP server with tool `approve`, Streamable HTTP mapped at `/mcp/approve`, loopback + bearer check, Access middleware skip for exactly this path from loopback), package `ModelContextProtocol.AspNetCore` in Server + THIRD-PARTY-NOTICES; tests `PermissionBrokerTests`, `ApprovalMcpTests` (`WebApplicationFactory`: remote IP not loopback → 404, wrong token → 404, round trip allow/deny through an MCP client from the SDK).
- [ ] The approval URL reaches the Application through a small port `IApprovalEndpoint { string? Url }` registered by the Server (`http://127.0.0.1:<Server:Port>/mcp/approve`); null (e.g. in Application tests) means the `ask` mode runs without the tool and logs a warning.
- [ ] Rules: `Bash` → `Bash(<command>)` exactly; other tools → tool name; duplicates not added. Timeout 10 min (fake clock) → deny; cancel → deny; server stopping → deny.
- [ ] FakeAgent gains an `--approve <tool> <json>` fixture step that calls the endpoint from `--mcp-config` (used in Task 9's E2E).
- [ ] Commit `Chat: approvals in the chat through an MCP tool`.

### Task 8: Client chat engine

**Files:** `src/AiChromeProxy.Client/Chat/ChatEngine.cs` (subscribe on connect/folder ready → `chat.open`; sessions list; per open session: events by seq, live `text` deltas assembled until `message` replaces them; catch-up with `chat.history` after reconnect; `SendAsync(text)`, `StopAsync()`, `ApproveAsync(requestId, decision)`, `NewChat()`, `Running`, `Elapsed`, `LastCost`, `Changed` event), DI in Client `Program.cs`; tests `ChatEngineTests` over `LoopbackServer` with the real `ChatService` and a fake runner.
- [ ] Tests: streamed answer assembled; reconnect mid-run → history catch-up without duplicates; busy surfaced as an error line; approve round trip; new chat vs continue; sessions list updates `running`.
- [ ] Commit `Client: chat engine`.

### Task 9: Chat UI, markdown, mermaid

**Files:** `src/AiChromeProxy.Client/Chat/MarkdownRenderer.cs` (Markdig pipeline: advanced extensions minus raw HTML; link filter keeps `http(s)` and turns `path:line` into `#open=<path>:<line>` links; fenced `mermaid` blocks become `<div class="mermaid-source" data-diagram="…">`), `Scripts/diagrams.ts` (`render(element)`: imports `/lib/mermaid/mermaid.min.js` once, `securityLevel: 'strict'`, theme from the shell's `data-theme`, error → source + message), `wwwroot/lib/mermaid/mermaid.min.js` (download the tarball from `https://registry.npmjs.org/mermaid/-/mermaid-<version>.tgz` with curl, extract `package/dist/mermaid.min.js`; no Node), `Shell/ChatView.razor`, `Shell/ChatInput.razor` (replaces the disabled input), sessions menu in `MainArea.razor`, status 2 in `SyncStatus`/right status component, settings tab additions (`ProjectSettingsForm` + view), CSS, `TabSet.ChatTab(sessionId)`; `BrowserScriptTests` allow-list for the vendored file; THIRD-PARTY-NOTICES (Markdig BSD-2, mermaid MIT); tests `MarkdownRendererTests` (no raw HTML, `javascript:` links dropped, `path:line` links, mermaid block marked), form/tab logic tests; E2E scenario with `Agent:Command` = FakeAgent: send → streamed answer, mermaid SVG present, tool row, permission card → **Allow**, **Stop**; screenshots dark/light 1280×720 checked (wait for transitions to end).
- [ ] Commit `Shell: chat tab with markdown, mermaid, tool rows and approvals`.

### Task 10: Docs

**Files:** `docs/chat.md` (setup: `claude` login on the home server for the service account, `Agent:*` options, permission modes, approval flow, sessions, limitations, manual checklist), `README.md` status line, `CLAUDE.md` (stack lines: chat, `ModelContextProtocol.AspNetCore`, Markdig, vendored mermaid), `docs/windows-host.md` (service account must be the one logged in to `claude`), architecture follow-ups table marks Origin/expiry done.
- [ ] Commit `Docs: Claude chat`.
