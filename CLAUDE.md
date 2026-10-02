# ai-chrome-proxy

Web app: Chrome on a locked-down machine syncs a repo folder to your home server, where Claude Code works on the mirror; the UI is a VS Code-like navigator + chat with mermaid diagrams.

Architecture and decisions: [docs/superpowers/specs/2026-10-02-architecture-design.md](docs/superpowers/specs/2026-10-02-architecture-design.md).

Open source (Apache-2.0). Everything in the repo is in English. No personal paths, domains or secrets in committed files — make it configurable.

## Stack

- .NET 10 (LTS, latest stable) and latest stable packages, C#, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- Minimal clean architecture (dependencies point inward; enforced by `tests/AiChromeProxy.Tests/Architecture`):
  - `src/AiChromeProxy.Domain` — wire contract (`Envelope`, `MessageTypes`), BCL only.
  - `src/AiChromeProxy.Application` → Domain — envelope routing and handlers; `AddApplication()`.
  - `src/AiChromeProxy.Infrastructure` → Application, Domain — Cloudflare Access options and token validator; `AddInfrastructure(IConfiguration)`.
  - `src/AiChromeProxy.Server` — ASP.NET Core host and composition root (hosts Client, one SignalR hub routing `Envelope` by `Type`, Access middleware).
  - `src/AiChromeProxy.Client` — Blazor WebAssembly → Domain only.
  - `src/AiChromeProxy.Tray` — Windows tray app (Avalonia, `net10.0-windows`, CommunityToolkit.Mvvm, Velopack) → Domain, Infrastructure only: service status/control (`IServiceControl`), elevated `--admin install|uninstall`, settings, logs, updates. See [docs/windows-host.md](docs/windows-host.md).
- Tests: `tests/AiChromeProxy.Tests` (xunit v3, CI; `net10.0-windows` because it covers the tray), `tests/AiChromeProxy.E2E` (Reqnroll + Playwright), `tests/load` (k6), `benchmarks/AiChromeProxy.Benchmarks` (BenchmarkDotNet) — see [docs/testing.md](docs/testing.md).
- JS only where C# can't: `fsaccess.js` (File System Access API), Monaco, mermaid.

## Process (SDD)

1. Sub-project spec → `docs/superpowers/specs/YYYY-MM-DD-<topic>-design.md`.
2. Plan → `docs/superpowers/plans/YYYY-MM-DD-<topic>.md`.
3. Implementation — subagent-driven development from the plan, final review.

Sub-projects in order: skeleton+transport → Windows host → sync → Claude chat → code navigator → mobile app (`mobile/`, Flutter).

## Code conventions

- All files are UTF-8 without BOM (enforced via `.editorconfig`). In PowerShell 5.1 pass `-Encoding utf8` explicitly when writing files.
- Search with ripgrep (`rg`), not `find` / `git grep` / `Select-String` — the primary dev environment is Windows.
- Style — `.editorconfig` + `StyleCop.ruleset` + `stylecop.json` (tabs, CRLF, no `this.` prefix, private fields `_camelCase`). StyleCop is applied to all projects via `Directory.Build.props`; rules with `Action="Error"` fail the build.
- Interfaces prefixed with `I`; async methods suffixed `Async`, returning `Task`/`Task<T>`.
- Logging — `ILogger<T>` via DI; the Server writes through Serilog (console + CLEF files in `<DataDir>\logs` when a data directory is in use).
- Windows interop that needs elevation or changes the machine (SCM, LSA, registry, Velopack) sits behind a seam (`IServiceControl`, `IAutoStart`, `IUpdateSource`) or in a thin `[ExcludeFromCodeCoverage]` class; the decisions it applies stay in tested code (`ServiceSetup`). Tests never install services, grant rights or write the registry.
- Pure logic (SyncEngine, manifest diff, hash-guard, path normalization, stream-json parser) has no browser/IO dependencies and is covered by xUnit.
- Never commit secrets: `appsettings.json` holds non-secret defaults only; per-machine values and secrets come from environment variables or `%ProgramData%\AiChromeProxy\appsettings.json` (written by the tray, outside the repo).

## Security

- The `Envelope` JSON shape (camelCase) is a public contract shared with the Flutter app — change it compatibly.

- Every path from the protocol is normalized and checked for escaping its root (mirror on the server, picked folder in the browser).
- Cloudflare Access is mandatory before exposing Server publicly: it runs `claude` with permission to edit files and execute commands.

## Tests and coverage

xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), coverage via `coverlet.MTP` (`tests/AiChromeProxy.Tests/testconfig.json`).

```bash
dotnet build -c Release
dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

Gate: `>= 85%` line coverage (coverlet exits non-zero below it). Gate failed — add tests, don't lower the threshold. Test failed — fix it, don't disable it.

E2E, load and benchmarks run locally only — setup and commands in [docs/testing.md](docs/testing.md).

## PR workflow

CI (`.github/workflows/ci.yml`, added in the skeleton sub-project): build (Release) → test with coverage → 85% gate. A PR isn't mergeable until CI is green. Before saying "done", check `gh pr checks <pr-number>`.

## CodeGraph

The repo is indexed by CodeGraph (`.codegraph/`). To find or understand code, use it FIRST, then grep/Read:

- MCP `codegraph_explore` — symbol source + call paths in one call.
- Shell: `codegraph explore "<symbol names or question>"`.
