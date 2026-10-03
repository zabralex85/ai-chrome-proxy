# Testing

Four layers. Only the first runs in CI; the others are local tools.

| Layer | Location | What it covers | Runs |
|---|---|---|---|
| Unit + integration + architecture | `tests/AiChromeProxy.Tests` (`Domain/`, `Application/`, `Infrastructure/`, `Server/`, `Tray/`, `Architecture/`) | Envelope contract, routing, handlers, Cloudflare Access validation and middleware, the real Server pipeline over SignalR (in-memory `TestServer`), host filtering and startup validation, data directory config + CLEF logging, tray view models / CLEF parser / update orchestration / service setup (fakes for the SCM, registry and Velopack), the tray windows on Avalonia headless, layer dependency rules (NetArchTest) | CI + local, 85% line-coverage gate |
| E2E BDD | `tests/AiChromeProxy.E2E` — Reqnroll + Playwright | The real app in a real browser: the shell renders and connects (ping latency in the pill and the connection box), theme toggle, left panel collapse, error list. The folder picker cannot be automated: sync is covered by the xunit hub/engine tests and the manual checklist in [sync.md](sync.md) | local |
| Load | `tests/load` — k6 | Concurrent SignalR connections pinging the hub; ping→pong latency and error rate | local |
| Micro-benchmarks | `benchmarks/AiChromeProxy.Benchmarks` — BenchmarkDotNet | Hot paths: `Envelope.Create`/serialization, `EnvelopeRouter.RouteAsync`, Access token validation | local |

Commands below run from the repo root. Shell snippets are Windows PowerShell 5.1 (`pwsh` works the same).

## Unit + integration + architecture (xunit v3)

xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), coverage via `coverlet.MTP`. The project targets `net10.0-windows` (it references the tray), so it runs on Windows only.

```powershell
dotnet build -c Release
dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

Gate: `>= 85%` line coverage. Gate failed — add tests, don't lower the threshold.

Browser code is strict TypeScript (`src/AiChromeProxy.Client/Scripts`, `tsconfig.json`) that `dotnet build` / `dotnet publish` compile to `wwwroot/js` (gitignored) with `Microsoft.TypeScript.MSBuild`, which bundles the native compiler — no Node.js needed. A type error fails the build; `Architecture/BrowserScriptTests.cs` fails on committed or hand-written `.js` under `src/*/wwwroot` and on `any` in `.ts`.

Architecture rules (`Architecture/LayerDependencyTests.cs`):

- Domain depends on nothing of ours and not on ASP.NET Core.
- Application does not depend on Infrastructure, Server, ASP.NET Core or `Microsoft.IdentityModel`.
- Infrastructure does not depend on Server or Client.
- Client depends on Domain only (not on Application, Infrastructure or Server).
- Tray depends only on Domain and Infrastructure (not on Application, Server, Client or ASP.NET Core).

Excluded from coverage (`testconfig.json` and `[ExcludeFromCodeCoverage]`): `Program.cs`, `*.razor`, view code-behind `*.axaml.cs` (UI glue; the windows are smoke-tested on Avalonia headless), and the members that change the machine or the network — `ServiceInstaller` (P/Invoke: SCM, LSA, LogonUser), `WindowsServiceControl` start/stop/install/uninstall, `RegistryAutoStart`, `VelopackUpdateSource`, `AdminCommand.RunElevatedAsync` (UAC). These are covered by the manual checklist in [windows-host.md](windows-host.md). `JsFolderAccess` (the interop wrapper over `Scripts/fsaccess.ts`) is excluded the same way and covered by the checklist in [sync.md](sync.md); the sync engine itself is tested against the real server-side sync through a loopback transport and an in-memory fake folder.

Tray tests never install services, grant rights, write the registry, show a desktop window or trigger UAC; read-only SCM queries (`EventLog` status, an unknown service) are fine.

Always pass `--project`: a bare `dotnet test` runs every test project in the solution, including E2E (which needs a browser).

## E2E (Reqnroll + Playwright)

`Features/Connection.feature` and `Features/Shell.feature` are bound by `StepDefinitions/ConnectionSteps.cs` and `StepDefinitions/ShellSteps.cs`. `Hooks/AppServer.cs` starts the real Server once per run via `WebApplicationFactory<Program>` on a Kestrel listener (`127.0.0.1`, random port, `Development`, `CloudflareAccess:Enabled=false`). `Hooks/BrowserHooks.cs` launches one Chromium per run and opens a fresh browser context per scenario. Selectors are `data-testid` attributes in `Pages/Home.razor` and `Shell/*.razor`.

One-time setup (downloads Chromium to `%LOCALAPPDATA%\ms-playwright`; repeat after a `Microsoft.Playwright` upgrade):

```powershell
dotnet build tests/AiChromeProxy.E2E
powershell -ExecutionPolicy Bypass -File tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
```

If the download fails after about 5 s with `Request to https://cdn.playwright.dev/... timed out after 30000ms`, the machine has a non-working IPv6 route (Playwright's downloader tries IPv6 first and Node reports the failed attempt as a timeout). Make it resolve IPv4 only for this one command:

```powershell
Set-Content -Encoding ascii "$env:TEMP\pw-ipv4only.cjs" "const d=require('dns'),l=d.promises.lookup;d.promises.lookup=(h,o)=>o&&o.family===6?Promise.resolve([]):l(h,o);"
$env:NODE_OPTIONS = "--require $env:TEMP\pw-ipv4only.cjs"
powershell -ExecutionPolicy Bypass -File tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
Remove-Item Env:NODE_OPTIONS
```

Run (headless):

```powershell
dotnet test --project tests/AiChromeProxy.E2E
```

Watch it in a visible browser:

```powershell
$env:HEADED = "1"; dotnet test --project tests/AiChromeProxy.E2E; Remove-Item Env:HEADED
```

Reqnroll generates the `*.feature.cs` code-behind into `obj/` (`ReqnrollUseIntermediateOutputPathForCodeBehind`); it carries an `<auto-generated>` header, so StyleCop skips it.

## Load (k6)

One-time setup: `winget install k6 --source winget`.

`tests/load/lib/signalr.js` speaks the SignalR JSON hub protocol over a plain WebSocket to `/hub` (no negotiate): handshake, `Send` invocations carrying a `ping` Envelope, `Receive` invocations parsed for the matching `pong`. Custom metrics: `pong_latency` (Trend, ms) and `ping_errors` (Rate; unanswered pings and failed connections). Each VU holds one connection for 10 s, pings once per second, then reconnects.

| Script | Shape | Thresholds |
|---|---|---|
| `smoke.js` | 2 VUs, 30 s | `pong_latency p(95) < 200 ms`, `checks rate > 0.99` |
| `load.js` | 0 → 200 connections over 1 min, hold 2 min, ramp down 30 s | `pong_latency p(95) < 500 ms`, `ping_errors rate < 1%` |

Terminal 1 — the server (Development, Access disabled):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"; $env:CloudflareAccess__Enabled = "false"
dotnet run -c Release --project src/AiChromeProxy.Server --no-launch-profile
```

Terminal 2:

```powershell
k6 run tests/load/smoke.js
k6 run tests/load/load.js
```

`BASE_URL` (default `http://127.0.0.1:5180`) points the scripts elsewhere. `DURATION_SCALE` scales the `load.js` stages for a quick check (`$env:DURATION_SCALE = "0.1"` runs about 21 s). k6 exits non-zero when a threshold fails. Stop the server with Ctrl+C.

## Micro-benchmarks (BenchmarkDotNet)

```powershell
dotnet run -c Release --project benchmarks/AiChromeProxy.Benchmarks -- --filter *
```

Classes (all `[MemoryDiagnoser]`): `EnvelopeBenchmarks` (`Create`, `Serialize`), `RouterBenchmarks` (`RoutePingAsync`, `RouteUnknownAsync`), `TokenValidationBenchmarks` (`ValidateAsync`: valid RS256 token, keys cached, stub JWKS handler, no network). Pick one with `--filter *Router*`; add `--job short` for a quick, less precise run. Results go to `BenchmarkDotNet.Artifacts/` (gitignored). No committed baselines.
