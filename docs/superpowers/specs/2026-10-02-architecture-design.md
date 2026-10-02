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

## Decisions

| Question | Decision | Why |
|---|---|---|
| Data direction | Source of truth is the folder on the client machine. Server keeps a mirror. | The code lives there. |
| Transport | WSS (SignalR) through Cloudflare Tunnel + Cloudflare Access. One hub, one channel: `Envelope { Type, Payload, CorrelationId }` routed by `Type` to handlers; client side behind `ITransport`. | TCP/443 passes corporate proxies; the tunnel handles NAT. WebRTC later, behind the same interface. |
| Running Claude | Spawn `claude -p --output-format stream-json --verbose` in the mirror folder; multi-turn via `--resume <sessionId>`. | Zero config duplication, works with a subscription. |
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

1. **Skeleton + transport** ([spec](2026-10-02-skeleton-transport-design.md)) — 4-project solution (StyleCop via `Directory.Build.props`), Server hosts Client, single SignalR hub with envelope routing + client `ITransport`, Cloudflare Access JWT check, CI (build → test → coverage ≥ 85% via `coverlet.runsettings`), cloudflared + Access setup guide.
2. **Sync** — fsaccess.js, SyncEngine, sync handlers, mirror, FileWatcher, hash-guard, conflict UI.
3. **Claude chat** — ClaudeRunner, chat handlers, streaming, markdown + mermaid.
4. **Code navigator** — tree, Monaco (read-only), highlighting via `path:line` / `path#Symbol`.

## Out of scope (MVP)

- WebRTC transport (`ITransport` seam kept).
- VS Code / Visual Studio extension.
- Manual editing in Monaco.
- Multiple repos at once.
- Custom "ui" MCP server.
