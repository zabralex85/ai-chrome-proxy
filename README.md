# ai-chrome-proxy

Use Claude Code on your home server to work on a repo that lives on a locked-down machine where only Chrome is available.

Chrome opens a web app (served via Cloudflare Tunnel + Access) that syncs the repo folder to the home server, gives you a VS Code-like navigator and a Claude chat with mermaid diagrams and code highlighting, and writes Claude's edits back.

**Status:** skeleton — transport + Cloudflare Access, Windows host (service, tray, installer), remote access wizard (Cloudflare Tunnel + Access in one step). See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md) and [Windows host](docs/windows-host.md).

## Install on a Windows home server

Download `AiChromeProxy-win-Setup.exe` from Releases: it installs a tray app that sets up the Server as a Windows service, edits its settings, shows its logs and installs updates. See [docs/windows-host.md](docs/windows-host.md).

## Run locally (no Cloudflare)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:CloudflareAccess__Enabled = "false"       # disables the Access check; honored only in Development
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
# open http://127.0.0.1:5180/
```

> You are responsible for complying with your organization's policies on moving code off a machine.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE). Bundled third-party software: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
