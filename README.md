# ai-chrome-proxy

Use Claude Code on your home server to work on a repo that lives on a locked-down machine where only Chrome is available.

Chrome opens a web app (served via Cloudflare Tunnel + Access) that syncs the repo folder to the home server, gives you a VS Code-like navigator and a Claude chat with mermaid diagrams and code highlighting, and writes Claude's edits back.

**Status:** design phase. See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md).

> You are responsible for complying with your organization's policies on moving code off a machine.

## License

MIT
