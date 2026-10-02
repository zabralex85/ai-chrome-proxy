# ai-chrome-proxy

Use Claude Code on your home server to work on a repo that lives on a locked-down machine where only Chrome is available.

Chrome opens a web app (served via Cloudflare Tunnel + Access) that syncs the repo folder to the home server, gives you a VS Code-like navigator and a Claude chat with mermaid diagrams and code highlighting, and writes Claude's edits back.

**Status:** skeleton — transport + Cloudflare Access. See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md) and [setup](docs/setup/cloudflare.md).

## Run locally (no Cloudflare)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:CloudflareAccess__Enabled = "false"       # disables the Access check; honored only in Development
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
# open http://127.0.0.1:5180/
```

> You are responsible for complying with your organization's policies on moving code off a machine.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
