# Sub-project 1 — Skeleton + Transport

Date: 2026-10-02. Parent: [architecture](2026-10-02-architecture-design.md).

## Goal

A runnable end-to-end skeleton: Chrome on any machine opens `https://<host>`, passes the Cloudflare Access login, sees "connected", and a Ping shows the round-trip time — over the same transport that sync and chat will use later. CI is green.

## Scope

In: solution layout, Server hosting the Blazor WASM Client, single SignalR hub with envelope routing, `ITransport` on the client, Cloudflare Access JWT validation, status page, CI with coverage gate, Cloudflare setup guide.

Out: sync, Claude, Monaco, WebRTC, Docker.

## Repository layout

```
AiChromeProxy.slnx
src/AiChromeProxy.Shared/    Envelope, message type constants
src/AiChromeProxy.Client/    Blazor WASM: ITransport, SignalRTransport, status page
src/AiChromeProxy.Server/    ASP.NET Core: hosts Client, TransportHub, Access JWT check
tests/AiChromeProxy.Tests/   xUnit
docs/setup/cloudflare.md     tunnel + Access + verification
.github/workflows/ci.yml
```

Target framework: `net10.0` for all projects. Home server: Windows, plain process (no Docker in this sub-project).

## Transport

### Wire contract (Shared)

```csharp
public sealed record Envelope(string Type, JsonElement Payload, string? CorrelationId = null);
```

- `Type` — message kind, e.g. `ping`, `pong`. Constants live in `Shared` (`MessageTypes`).
- `Payload` — per-type JSON object.
- `CorrelationId` — set by the sender of a request, echoed in the response.

One SignalR hub at `/hub`:
- client → server: `Send(Envelope)`;
- server → client: `Receive(Envelope)`.

This is the single channel for everything (sync and chat later). Swapping SignalR for WebRTC means replacing `ITransport` and the hub, not the message handlers.

### Client

```csharp
public interface ITransport : IAsyncDisposable
{
	TransportState State { get; }               // Disconnected, Connecting, Connected, Reconnecting
	event Action<TransportState>? StateChanged;
	event Action<Envelope>? Received;
	Task ConnectAsync(CancellationToken ct = default);
	Task SendAsync(Envelope envelope, CancellationToken ct = default);
}
```

`SignalRTransport` implements it on `HubConnection` with `WithAutomaticReconnect()`. The hub URL is relative (`/hub`) — Client is served from the same origin, and the browser sends the `CF_Authorization` cookie automatically.

### Server

- `TransportHub.Send(Envelope)` dispatches by `Type` to the registered `IEnvelopeHandler` and sends any returned `Envelope` back to the caller via `Receive`.
- `IEnvelopeHandler { string Type { get; } Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct); }` — registered in DI; the router builds a `Type → handler` map at startup (duplicate `Type` = startup error).
- Unknown `Type` → reply `error` envelope `{ "code": "unknown_type", "type": "<type>" }` with the request's `CorrelationId`.
- The only handler in this sub-project: `PingHandler` — `ping` `{}` → `pong` `{ "serverTime": "<ISO-8601 UTC>" }`.

## Security

### Network binding

Kestrel listens on `127.0.0.1:<Server:Port>` only (default `5180`). Reached exclusively through `cloudflared` running on the same machine.

### Cloudflare Access JWT validation

`CloudflareAccessMiddleware`, runs before static files and the hub:

1. Token source: header `Cf-Access-Jwt-Assertion`; fallback cookie `CF_Authorization`.
2. Validate with `Microsoft.IdentityModel.JsonWebTokens`:
   - signature against JWKS from `https://<TeamDomain>/cdn-cgi/access/certs`, fetched through a named `HttpClient` (`"cf-access-jwks"`) — tests swap its `HttpMessageHandler` for a stub serving a test key, no extra interface; keys cached, refetched when a token carries an unknown `kid`, at most once per minute;
   - `iss` == `https://<TeamDomain>`;
   - `aud` contains `Audience`;
   - `exp` / `nbf` with 1 min clock skew.
3. Invalid or missing → `401`, including the WebSocket upgrade request.

Configuration:

| Key | Meaning |
|---|---|
| `CloudflareAccess:TeamDomain` | e.g. `myteam.cloudflareaccess.com` |
| `CloudflareAccess:Audience` | Access application AUD tag |
| `CloudflareAccess:Enabled` | default `true`; `false` honored **only** when `ASPNETCORE_ENVIRONMENT=Development` |

Fail closed: outside Development, if `Enabled` is false or `TeamDomain`/`Audience` is empty, the Server refuses to start with a clear error.

Secrets: none — TeamDomain and AUD are not secret, but they are per-user, so they come from environment variables (`CloudflareAccess__TeamDomain`, `CloudflareAccess__Audience`), never from committed `appsettings.json`. No `appsettings.Local.json` loading: a local file would silently override test settings; the Windows host sub-project defines the persistent config location.

## UI

One page (`/`):
- connection state badge: Connected / Connecting / Reconnecting / Disconnected;
- "Ping" button → shows round-trip time in ms and server time from `pong`.

No styling work, no layout beyond that.

## Tooling (verified on a prototype)

- `global.json`: SDK `10.0.100` with `rollForward: latestFeature`; `"test": { "runner": "Microsoft.Testing.Platform" }` — required by xunit v3 on the .NET 10 SDK.
- Latest stable packages at the time of writing: ASP.NET Core / SignalR `10.0.x`, `Microsoft.IdentityModel.JsonWebTokens` `8.x`, `xunit.v3` `4.x`, `Microsoft.NET.Test.Sdk` `18.x`, `coverlet.MTP` `10.x`.
- `StyleCop.Analyzers` `1.2.0-beta.556` (1.1.118 does not understand records / target-typed `new`). `SA1412` (BOM) set to `None` — repo is UTF-8 without BOM.
- Code style enforced by the ruleset: no `this.` prefix (`SX1101`), private fields `_camelCase`.
- Coverage: `coverlet.MTP` configured in `tests/AiChromeProxy.Tests/testconfig.json` (include `[AiChromeProxy.*]*`, exclude the test assembly, exclude by file `**/Program.cs,**/*.razor`, cobertura). Threshold enforced by coverlet itself. `coverlet.runsettings` (VSTest-only) is deleted.

## CI

`.github/workflows/ci.yml`, on push and pull_request, `windows-latest`:

1. `actions/setup-dotnet` with `global-json-file: global.json`.
2. `dotnet restore`.
3. `dotnet build -c Release --no-restore` (StyleCop `Error` rules fail the build).
4. `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` — non-zero exit when line coverage < 85%.

## Testing

| Test | What it proves |
|---|---|
| Router: known type → handler called, reply returned | dispatch works |
| Router: unknown type → `error` with `unknown_type` and same `CorrelationId` | error contract |
| Router: duplicate handler `Type` → startup exception | misconfig caught early |
| `PingHandler` → `pong` with parseable UTC `serverTime` | handler contract |
| Middleware (RSA key generated in test, JWKS served by stub `HttpMessageHandler`): valid token → passes | happy path |
| Middleware: wrong `aud`, wrong `iss`, expired, foreign key, missing token → 401 | rejection paths |
| Middleware: token in `CF_Authorization` cookie only → passes | cookie fallback |
| Middleware: Development + `Enabled=false` → passes without token | dev mode |
| Startup: non-Development + empty config → fails to start (test sets empty values explicitly so real env vars on the machine can't mask it) | fail closed |
| Hub via `WebApplicationFactory` + SignalR client over WebSocket: `ping` → `pong` | end-to-end transport |
| Hub without token (Access enabled) → connection rejected | hub is protected |

`SignalRTransport` (browser-side) is covered by the end-to-end hub test from the .NET SignalR client plus a manual check in Chrome.

## Setup guide (`docs/setup/cloudflare.md`)

1. Install `cloudflared` on the home server (Windows) and run it as a service.
2. Create a tunnel; public hostname → `http://127.0.0.1:5180`.
3. Zero Trust → Access → Applications → Self-hosted for that hostname; policy: allow your email(s).
4. Copy the application's AUD tag; set `CloudflareAccess__TeamDomain` / `CloudflareAccess__Audience`.
5. Run the Server; open the hostname in Chrome; log in; verify "Connected" and Ping.
6. Verify protection: `curl http://127.0.0.1:5180/` without a token → 401.

## Done when

- Chrome on another machine: Access login → "Connected" → Ping shows RTT.
- Direct request without a valid Access token → 401.
- CI green with the 85% gate.
