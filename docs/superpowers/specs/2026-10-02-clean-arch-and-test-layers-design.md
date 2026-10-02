# Sub-project 1.5 — Minimal clean architecture + test layers

Date: 2026-10-02. Parent: [architecture](2026-10-02-architecture-design.md). Builds on the merged [skeleton](2026-10-02-skeleton-transport-design.md).

## Goal

1. Restructure the server side into a minimal clean architecture before more features land on it.
2. Add the missing test layers next to the existing xunit suite: E2E BDD (Reqnroll + Playwright), load (k6), micro-benchmarks (BenchmarkDotNet).

No new product behavior.

## Layers

```
AiChromeProxy.Domain          no dependencies (BCL only)
  Envelope, MessageTypes      public wire contract (+ future core entities such as sync manifest entries)
AiChromeProxy.Application     -> Domain
  IEnvelopeHandler, EnvelopeRouter, PingHandler, DependencyInjection.AddApplication()
AiChromeProxy.Infrastructure  -> Application, Domain
  CloudflareAccessOptions, CloudflareAccessTokenValidator, DependencyInjection.AddInfrastructure(IConfiguration)
AiChromeProxy.Server          host / composition root -> Application, Infrastructure, Domain, Client
  Program, TransportHub, CloudflareAccessMiddleware, WASM hosting
AiChromeProxy.Client          Blazor WASM -> Domain only
```

Rules:
- The `AiChromeProxy.Shared` project is renamed to `AiChromeProxy.Domain` (namespace `AiChromeProxy.Domain`). The JSON shape of `Envelope` does not change (camelCase public contract).
- Moving code between projects keeps behavior identical; only namespaces and registrations change.
- `AddApplication()` registers `TimeProvider.System` (if not registered), `PingHandler` as `IEnvelopeHandler`, and `EnvelopeRouter` — all singletons, as today.
- `AddInfrastructure(IConfiguration)` binds `CloudflareAccessOptions` from section `CloudflareAccess`, registers the named HttpClient `cf-access-jwks` and `CloudflareAccessTokenValidator` (singleton).
- No port interface for the token validator: Application never calls it; the host (composition root) references Infrastructure directly. Add a port only when Application needs one.
- `CloudflareAccessMiddleware` stays in the host: it is HTTP plumbing.

### Architecture tests

In `tests/AiChromeProxy.Tests/Architecture/` using `NetArchTest.Rules`:

| Rule | Assertion |
|---|---|
| Domain is pure | `AiChromeProxy.Domain` has no dependency on `AiChromeProxy.Application`, `AiChromeProxy.Infrastructure`, `AiChromeProxy.Server`, `AiChromeProxy.Client`, `Microsoft.AspNetCore` |
| Application is framework-free | `AiChromeProxy.Application` has no dependency on `AiChromeProxy.Infrastructure`, `AiChromeProxy.Server`, `Microsoft.AspNetCore`, `Microsoft.IdentityModel` |
| Infrastructure doesn't reach up | `AiChromeProxy.Infrastructure` has no dependency on `AiChromeProxy.Server`, `AiChromeProxy.Client` |
| Client talks contracts only | `AiChromeProxy.Client` has no dependency on `AiChromeProxy.Application`, `AiChromeProxy.Infrastructure`, `AiChromeProxy.Server` |

## Test layers

| Layer | Location | Content | Runs |
|---|---|---|---|
| Unit + integration + architecture | `tests/AiChromeProxy.Tests` (folders mirror layers: `Domain/`, `Application/`, `Infrastructure/`, `Server/`, `Architecture/`) | existing 39 tests (moved, unchanged) + architecture tests | CI + local; 85% line-coverage gate |
| E2E BDD | `tests/AiChromeProxy.E2E` — Reqnroll + Microsoft.Playwright | `Features/*.feature`, C# step definitions, hooks | local only |
| Load | `tests/load/` — k6 scripts | SignalR JSON protocol over WebSocket | local only |
| Micro-benchmarks | `benchmarks/AiChromeProxy.Benchmarks` — BenchmarkDotNet console app | hot paths | local only |

CI (`.github/workflows/ci.yml`) changes only so that it keeps running exactly the xunit project with the coverage gate; E2E and benchmark projects are built (they are in the solution, so StyleCop applies) but not executed.

### E2E (Reqnroll + Playwright)

- Server under test: started in-process by a hook via `WebApplicationFactory<Program>` on a **real Kestrel** listener (random free port), environment `Development`, `CloudflareAccess:Enabled=false`. One server per test run.
- Browser: Playwright Chromium, headless by default; `HEADED=1` env var opens a visible browser for debugging.
- Initial scenarios (`Features/Connection.feature`):
  - *Status page connects* — Given the server is running, When I open the app, Then the connection state is "Connected".
  - *Ping shows a round trip* — Given the app is connected, When I click "Ping", Then I see "Pong in <n> ms" with a server time.
- Selectors: stable `data-testid` attributes added to `Home.razor` (`connection-state`, `ping-button`, `ping-result`) — the only product-code change outside the restructure.
- Auth is not exercised here (covered by xunit integration tests).

### Load (k6)

- `tests/load/lib/signalr.js`: minimal SignalR JSON-protocol helper — negotiate-less WebSocket connect to `/hub`, handshake `{"protocol":"json","version":1}\x1e`, `Send` invocation with an `Envelope`, parse `Receive` invocations, record ping→pong latency as a custom `Trend`.
- `tests/load/smoke.js`: 2 VUs, 30 s, each VU pings once per second. Thresholds: `pong_latency p(95) < 200 ms`, `checks rate > 0.99`.
- `tests/load/load.js`: ramp 0 → 200 concurrent connections over 1 min, hold 2 min, ramp down; same metrics, thresholds `p(95) < 500 ms`, error rate < 1%.
- Target: `BASE_URL` env var (default `http://127.0.0.1:5180`) against a server started with `dotnet run` in Development with Access disabled.

### Benchmarks (BenchmarkDotNet)

`benchmarks/AiChromeProxy.Benchmarks` (console, `[MemoryDiagnoser]`):
- `EnvelopeBenchmarks`: `Envelope.Create` with a small DTO; serialize an `Envelope` to JSON with web options.
- `RouterBenchmarks`: `EnvelopeRouter.RouteAsync` for a known type (ping) and an unknown type.
- `TokenValidationBenchmarks`: `CloudflareAccessTokenValidator.ValidateAsync` for a valid RS256 token with keys already cached (in-process RSA key + stub JWKS handler, no network).

Run: `dotnet run -c Release --project benchmarks/AiChromeProxy.Benchmarks -- --filter *`. Results go to `BenchmarkDotNet.Artifacts/` (gitignored). No committed baselines.

## Documentation

`docs/testing.md`: what each layer covers and the exact local commands, plus one-time setup (`pwsh tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium`, `winget install k6`). `CLAUDE.md` links to it and lists the new projects.

## Risks to verify with a prototype before planning

1. Reqnroll on xunit v3 / Microsoft.Testing.Platform (repo opts into MTP via `global.json`). Fallback: the E2E project uses whichever Reqnroll test-framework adapter works with MTP on .NET 10; it is isolated, so this does not affect the unit suite.
2. `WebApplicationFactory` with a real Kestrel listener on .NET 10 (`UseKestrel()`), so a real browser can connect.
3. StyleCop on Reqnroll-generated code (expected to be skipped as auto-generated).
4. NetArchTest support for .NET 10 assemblies.

## Done when

- Solution builds with 0 errors/0 warnings; the xunit suite (existing tests + architecture tests) passes the 85% gate in CI.
- `dotnet test --project tests/AiChromeProxy.E2E` passes both scenarios locally (headless).
- `k6 run tests/load/smoke.js` passes its thresholds against a local dev server.
- The benchmark app runs all three benchmark classes.
- `docs/testing.md` describes all four layers.

## Out of scope

- Running E2E, load or benchmarks in CI.
- Auth in E2E/load (Access disabled; covered by xunit).
- Performance budgets or committed benchmark baselines.
