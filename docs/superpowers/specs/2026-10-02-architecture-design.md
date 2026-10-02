# ai-chrome-proxy — Architecture

Date: 2026-10-02. Status: architecture agreed; each sub-project gets its own detailed spec.

## Goal

Work on a repository that lives on a locked-down machine (e.g. an RDP server where only Chrome is available and nothing can be installed) using Claude Code running on your own home server — with whatever Claude Code setup you have there (plugins, MCP servers such as codegraph, `CLAUDE.md`).

Chrome opens a web app served by the home server (through Cloudflare). The app:
1. syncs the repo folder from the locked-down machine to a mirror on the home server;
2. provides a VS Code-like UI: file tree, code viewer, chat with Claude;
3. renders mermaid diagrams and highlights classes/lines when Claude references them;
4. writes Claude's edits back to the folder on the locked-down machine.

## Constraints

- Client machine: Chrome only (recent, File System Access API available). No installs.
- Repo size: up to ~20k files.
- Stack: C# / .NET 10. UI — Blazor WebAssembly.
- Claude: the existing Claude Code CLI on the home server (subscription, plugins, MCP, `~/.claude` — used as-is).
- Home server: Windows first. Must work after power-on **without anyone logging into Windows**. macOS (e.g. a Mac mini running a local model) later — Server is plain cross-platform .NET.
- Model-agnostic: the same setup must work with a local model (Qwen via Ollama, Gemma via LM Studio, …) instead of Anthropic's API.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Data direction | Source of truth is the folder on the client machine. Server keeps a mirror. | The code lives there. |
| Transport | WSS (SignalR) through Cloudflare Tunnel + Cloudflare Access. One hub, one channel: `Envelope { Type, Payload, CorrelationId }` routed by `Type` to handlers; client side behind `ITransport`. | TCP/443 passes corporate proxies; the tunnel handles NAT. WebRTC later, behind the same interface. |
| Running Claude | Spawn `claude -p --output-format stream-json --verbose` in the mirror folder; multi-turn via `--resume <sessionId>`. Command, args and extra env come from config (`Agent:Command`, `Agent:Args`, `Agent:Env`). | Zero config duplication, works with a subscription. |
| Local models | Configuration only: `Agent:Env` sets `ANTHROPIC_BASE_URL` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_MODEL` so Claude Code talks to a local Anthropic-compatible endpoint (Ollama, LM Studio, …); for OpenAI-only servers put a LiteLLM proxy in front. A different agent CLI later goes behind an `IAgentRunner` seam. | No second runner to build or maintain. |
| Hosting on the home server | Server runs as an OS service with auto-start (Windows Service now; launchd/systemd later). **Runs under the user's own account**, not LocalSystem, so `claude` sees the user's `~/.claude` login, plugins and MCP. Logs go to files (`%ProgramData%\AiChromeProxy\logs` on Windows). | Works after power-on with nobody logged in. |
| Tray app | Avalonia (cross-platform) tray: install/uninstall/start/stop the service (asks for the account password, grants "Log on as a service"), shows status, browses current and historical logs, opens the UI. OS-specific service management behind an interface. | One UI for Windows now and macOS/Linux later. |
| Privacy modes | (1) Cloud model — code goes to the model provider. (2) Fully local — model on the home server, code must not leave the user's machines. **Cloudflare Tunnel terminates TLS at Cloudflare's edge**, so in mode 2 transit through it is not private. Fully-local mode therefore needs end-to-end encryption: WebRTC DataChannel (DTLS browser↔server; Cloudflare only for signaling/TURN, sees ciphertext) or app-level encryption of `Envelope`s over WSS. Both behind `ITransport`; not in MVP. | Honest threat model; the skeleton needs no change. |
| Releases & install | Tag `v*` → GitHub Actions builds a GitHub Release with a single installer (`Setup.exe`, Velopack: install + auto-update, cross-platform later) containing Server + tray. A developer installs everything on the home server with that one setup; the tray's first-run wizard then: checks `claude` CLI (or a local-model endpoint), installs `cloudflared` and creates the tunnel (`cloudflared tunnel login` / `tunnel create` / `route dns`), collects Access team domain + AUD, asks the account password and installs the service. Persistent config lives in `%ProgramData%\AiChromeProxy\`. | One-setup install for a regular developer. |
| Mobile app | Flutter app in `mobile/` (same repo): chat with the home agent, watch progress, notifications. Uses the same `/hub` + `Envelope` protocol (Dart SignalR client), so the `Envelope` JSON shape (camelCase) is a **public, language-neutral contract** — C# types are one implementation of it. Auth: Cloudflare Access **service token** (`CF-Access-Client-Id` / `CF-Access-Client-Secret`); Access turns it into the same JWT, so the Server middleware is unchanged. Push when the app is closed: FCM/APNs vs UnifiedPush/ntfy — decided in its own spec. | Reuses transport and auth; no second API. |
| Distribution model | Fully open source and self-hostable (BYO: own Cloudflare tunnel/Access, own Firebase for push, own model key or local model). Optional hosted offering later (managed relay instead of own Cloudflare, push relay instead of own Firebase, store-published mobile app). Every external service is reached through config + an interface (`CloudflareAccess:*`, `ITransport`, `IPushSender` in the mobile sub-project) so self-host and hosted differ only in configuration. Hosted services themselves are out of scope. | Same pattern as other open-core agent tools; no lock-in for self-hosters. |
| Rust | Not used now. Candidates later: rsync-style block deltas in the browser (Rust→WASM, e.g. `fast_rsync`) if large-file sync becomes a bottleneck; a WebRTC sidecar (`webrtc-rs`). Both sit behind existing seams (SyncEngine, `ITransport`). | A second toolchain costs more than it buys today; hashing uses `crypto.subtle`. |
| Conflicts | Auto-write Claude's edits to the client + hash-guard: write only if the file's current hash == `baseHash`. Otherwise show a conflict in the UI and leave the file untouched. | Speed without losing manual edits. |
| Change detection on the client | `FileSystemObserver` where available + periodic full mtime scan as a safety net. | A full scan of <20k files is cheap. |
| Blazor model | WebAssembly, hosted by the ASP.NET Core server. | Sync runs in the browser → C# only via WASM. UI doesn't round-trip home on every click. |
| Claude ↔ UI contract | Convention via `--append-system-prompt`: diagrams as ```` ```mermaid
flowchart LR
  subgraph CLIENT["Client machine: Chrome"]
    FS[(Repo folder)]
    JS[fsaccess.js<br/>FS Access API + FileSystemObserver]
    subgraph WASM["Blazor WASM (Client)"]
      SE[SyncEngine<br/>scan / hash / diff / guard]
      UI[UI: tree · viewer · chat · mermaid]
      TR[ITransport]
    end
    FS <--> JS <--> SE
    SE <--> TR
    UI <--> TR
  end
  subgraph HOME["Home server (.NET 10)"]
    HUB[TransportHub · SignalR<br/>routes Envelope by Type]
    SH[Sync handlers]
    CH[Chat handlers]
    MIR[(Mirror data/&lt;repo&gt;)]
    W[Mirror FileWatcher]
    CR[ClaudeRunner<br/>claude -p stream-json]
  end
  TR <-- "WSS · CF Tunnel + Access" --> HUB
  HUB <--> SH
  HUB <--> CH
  SH --> MIR
  CR -- edits --> MIR
  MIR --> W -- "{path, content, baseHash}" --> SH
  CH <--> CR
```

### Projects (solution)

| Project | Type | Responsibility |
|---|---|---|
| `Shared` | classlib | Protocol DTOs (manifest, chunks, edits, chat events). No dependencies. |
| `Client` | Blazor WASM | SyncEngine (pure C#), UI, JS interop: `fsaccess.js`, Monaco (`BlazorMonaco`), mermaid. |
| `Server` | ASP.NET Core | Hosts Client. TransportHub + envelope handlers (sync, chat), mirror (`Mirror:Root`, default `data/`, gitignored), FileWatcher, ClaudeRunner. |
| `Tests` | xUnit | SyncEngine logic, protocol, ClaudeRunner stream-json parser, server hubs (`WebApplicationFactory`). |

### Sync flow

1. "Open folder" → `showDirectoryPicker()`; the handle is stored in IndexedDB (after reload only `requestPermission` is needed).
2. Initial scan: manifest `{path, size, mtime, sha256}` honoring `.gitignore` plus a built-in exclude list (`.git/`, `node_modules/`, `bin/`, `obj/`) → server.
3. Server diffs against the mirror → replies with missing/changed paths → browser uploads their content.
4. Then: `FileSystemObserver` events + periodic scan → incremental deltas.
5. Back channel: the mirror FileWatcher sees an edit not caused by sync → `{path, content, baseHash}` → browser checks the hash on the client → writes or raises a conflict.

### Chat flow

1. UI message → TransportHub → chat handler → ClaudeRunner spawns in the mirror folder (`--resume` to continue the session).
2. stream-json events → chat handler → TransportHub → UI (streamed text, tool-use status).
3. UI renders markdown; `mermaid` blocks → `mermaid.render`; `path:line` / `path#Symbol` refs → click opens the file and highlights.

## Security

- **Cloudflare Access is mandatory.** The server runs Claude with permission to edit files and execute commands on the home server — an open endpoint is remote code execution.
- Every path from the protocol is normalized and checked: it must resolve inside the mirror root (server) and inside the picked folder (browser). Reject `..`, absolute paths, `\` inside segments.
- `.env`, keys and other secrets: default exclude list + `.gitignore`. Only non-excluded files are synced.
- README carries a neutral disclaimer: users are responsible for complying with their organization's policies on moving code off a machine.

## Error handling

- WSS drop: SignalR auto-reconnect → after reconnect, manifests are exchanged again (server decides what to re-request). Sync state is not kept in memory across sessions.
- Hash-guard rejection: the edit is not written; the UI shows the conflict with a diff (keep client version / take Claude's version).
- Lost folder permission: UI blocks with a "Restore access" button.
- `claude` crash/timeout: error shown in chat, process killed, session continues with the next message.

## Testing

- SyncEngine — pure logic (manifest diff, guard, exclude matching, path normalization) → xUnit, no browser.
- ClaudeRunner stream-json parser → xUnit on recorded fixtures.
- Hubs → `WebApplicationFactory` + SignalR client.
- `fsaccess.js` and UI — manual check in Chrome; Playwright once the UI stabilizes.

## Decomposition (implementation order)

Each sub-project: its own spec → plan → SDD.

1. **Skeleton + transport** ([spec](2026-10-02-skeleton-transport-design.md)) — 4-project solution (StyleCop via `Directory.Build.props`), Server hosts Client, single SignalR hub with envelope routing + client `ITransport`, Cloudflare Access JWT check, CI (build → test → coverage ≥ 85% via coverlet.MTP threshold), cloudflared + Access setup guide.
2. **Windows host** — Server as a Windows Service under the user's account (auto-start, no login needed), file logging, persistent config in `%ProgramData%`, Avalonia tray: first-run wizard, install/uninstall/start/stop, status, log viewer (current + historical), open UI; Velopack `Setup.exe` built by a tag-triggered release workflow.
3. **Sync** — fsaccess.js, SyncEngine, sync handlers, mirror, FileWatcher, hash-guard, conflict UI.
4. **Claude chat** — ClaudeRunner (configurable `Agent:*`, local-model ready), chat handlers, streaming, markdown + mermaid.
5. **Code navigator** — tree, Monaco (read-only), highlighting via `path:line` / `path#Symbol`.
6. **Mobile app** (`mobile/`, Flutter) — chat, progress, notifications over the same hub; Access service-token auth; push channel choice.

## Required follow-ups from the skeleton review

Found in the whole-branch review of sub-project 1; each must land in the sub-project named, no later.

| Follow-up | Why | Lands in |
|---|---|---|
| Handler contract: `HandleAsync(Envelope, EnvelopeContext ctx, ct)` with connection id, Access identity (email from the JWT) and `ctx.SendAsync` for server-initiated messages; long-running work off the hub invocation (SignalR runs one invocation per client at a time by default). | Streaming agent events and pushing file changes need server→client sends; a long agent run must not block cancel/ping. | Sync (first spec after Windows host) |
| Error contract: hub catches handler failures and replies `error {code, message?}` with the request's `correlationId` (`bad_request` for null/empty `Type`, `internal` for exceptions). Client `RequestAsync(envelope, timeout)` helper owns correlation. | Today a failing handler or a null `Type` leaves the caller waiting forever. | Sync |
| Message size: keep SignalR's 32 KB `MaximumReceiveMessageSize`; chunk file content explicitly. | DoS-safe default; sync must not raise it blindly. | Sync |
| Reconnect: the default retry policy gives up after ~42 s; sync needs indefinite reconnect + manifest re-exchange. | Long-lived sessions over a home connection. | Sync |
| `Origin` allow-list on `/hub*` against the configured public hostname; docs: Access cookie HttpOnly + SameSite=Lax/Strict + binding cookie. | Auth rides on an ambient cookie; WebSockets aren't covered by CORS → cross-site WebSocket hijacking = RCE once the agent runs commands. | Before Claude chat |
| Abort a connection when its Access token `exp` passes (store `exp` at upgrade time). | An open socket otherwise outlives the Access session/revocation. | Before Claude chat |
| `AllowedHosts` restricted to the public host + `127.0.0.1`. | DNS-rebinding hardening on the home machine. | Windows host |
| JWKS key TTL (coarse periodic refresh); monotonic clock for the throttle (`GetTimestamp`/`GetElapsedTime`); TeamDomain host-format check (wizard). | Retired keys stay trusted until restart; clock jumps stall refresh; misconfig only surfaces at first request. | Windows host |

## Out of scope (MVP)

- WebRTC transport / end-to-end encryption for fully-local privacy mode (`ITransport` seam kept).
- VS Code / Visual Studio extension.
- Manual editing in Monaco.
- Multiple repos at once.
- Custom "ui" MCP server.
- macOS/Linux service install (launchd/systemd) — after Windows host, behind the same interface.
- Docker image.
