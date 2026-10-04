# ai-chrome-proxy

Use Claude Code on your home server to work on a repo that lives on a locked-down machine where only Chrome is available.

Chrome opens a web app (served via Cloudflare Tunnel + Access) that syncs the repo folder to the home server, gives you a VS Code-like navigator and a Claude chat with mermaid diagrams and code highlighting, and writes Claude's edits back.

**Status:** transport + Cloudflare Access, Windows host (service, tray, installer), remote access wizard (Cloudflare Tunnel + Access in one step), folder sync with a back channel (server edits come back to the folder), the app shell (needs Chrome 123+) and a Claude chat with mermaid diagrams, tool rows and approvals, and a read-only code viewer (Monaco, `path:line` / `path#Symbol` links) tree actions (create, rename, delete from a context menu) and diagram tools (full-window zoomable viewer, save source / SVG / PNG into the project or download). See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md), [Windows host](docs/windows-host.md), [sync](docs/sync.md), [chat](docs/chat.md), [navigator](docs/navigator.md) and the [compatibility check page](docs/compat-check.md) (can a given browser run the app).

## Install on a Windows home server

Download `AiChromeProxy-win-Setup.exe` from Releases: it installs a tray app that sets up the Server as a Windows service, edits its settings, shows its logs and installs updates. See [docs/windows-host.md](docs/windows-host.md).

## Run locally (no Cloudflare)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:CloudflareAccess__Enabled = "false"       # disables the Access check; honored only in Development
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
# open http://127.0.0.1:5180/ - a folder you open there is mirrored to src/AiChromeProxy.Server/data/mirror (see docs/sync.md)
```

## Long paths on Windows

The mirror can hold paths longer than 260 characters. The Server handles them, but git and builds run in the mirror need long paths enabled: set *Enable Win32 long paths* (`HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1`) and restart the service, run `git config --system core.longpaths true`, and consider a short `Mirror:Root` such as `C:\m` (create and restrict it yourself; not a `subst` or network drive). Details: [docs/sync.md](docs/sync.md#long-paths-on-windows).

> You are responsible for complying with your organization's policies on moving code off a machine.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE). Bundled third-party software: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
