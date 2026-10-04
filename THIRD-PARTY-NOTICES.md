# Third-party notices

The Windows release (`AiChromeProxy-win-Setup.exe`) bundles the following third-party software, unmodified.

## cloudflared

- File: `server\cloudflared.exe` (`cloudflared-windows-amd64.exe`, version 2026.9.3, SHA256 `f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2`)
- Copyright: Cloudflare, Inc.
- License: Apache License 2.0 — full text in `licenses\cloudflared-LICENSE.txt` (next to this file in the package; https://github.com/cloudflare/cloudflared/blob/master/LICENSE)
- Source: https://github.com/cloudflare/cloudflared (tag `2026.9.3`)

The Server starts it as a child process to connect the Cloudflare Tunnel configured by the tray's **Set up remote access…** wizard.

## SQLite storage (Microsoft.Data.Sqlite)

- Files: `Microsoft.Data.Sqlite.dll`, `Microsoft.Data.Sqlite.Core.dll`, `SQLitePCLRaw.*.dll` and the native `e_sqlite3.dll` (in the Server folder, and in the tray package, which carries them through the Infrastructure library it references), unmodified.
- Microsoft.Data.Sqlite 10.0.12 — Copyright Microsoft Corporation; License: MIT (https://licenses.nuget.org/MIT)
- SQLitePCLRaw 2.1.12 (`bundle_e_sqlite3`, `lib.e_sqlite3`, `provider.e_sqlite3`, `core`) — Copyright 2014-2024 SourceGear, LLC; License: Apache License 2.0 (https://licenses.nuget.org/Apache-2.0)
- SQLite (native library inside `e_sqlite3.dll`) — public domain (https://www.sqlite.org/copyright.html)

The Server keeps the agreed hash per synced file and the project settings in `aicp.db` in the data folder.

## MCP C# SDK (ModelContextProtocol.AspNetCore)

- Files: `ModelContextProtocol.AspNetCore.dll`, `ModelContextProtocol.dll`, `ModelContextProtocol.Core.dll` and `Microsoft.Extensions.AI.Abstractions.dll` (in the Server folder), unmodified.
- ModelContextProtocol.AspNetCore, ModelContextProtocol and ModelContextProtocol.Core 2.2.0 — Copyright Model Context Protocol a Series of LF Projects, LLC; License: Apache License 2.0 (https://licenses.nuget.org/Apache-2.0)
- Microsoft.Extensions.AI.Abstractions 10.8.3 (a dependency of ModelContextProtocol.Core) — Copyright Microsoft Corporation; License: MIT (https://licenses.nuget.org/MIT)
- Source: https://github.com/modelcontextprotocol/csharp-sdk (tag `v2.2.0`)

The Server hosts the loopback-only approval endpoint (`/mcp/approve`) that Claude Code calls to ask for permissions in the chat.

## Markdig

- File: `Markdig.dll` (in the Client's WebAssembly files, served by the Server), unmodified.
- Markdig 1.4.0 — Copyright (c) Alexandre Mutel; License: BSD 2-Clause (https://licenses.nuget.org/BSD-2-Clause)
- Source: https://github.com/xoofx/markdig

The browser renders Claude's Markdown answers with it (raw HTML switched off).

## mermaid

- File: `lib\mermaid\mermaid.min.js` (served at `/lib/mermaid/mermaid.min.js`; committed as `src/AiChromeProxy.Client/wwwroot/lib/mermaid/mermaid.min.js`, `dist/mermaid.min.js` of the npm package `mermaid` 12.1.0, unmodified); the licence text is next to it in `LICENSE`
- Copyright (c) 2014 - 2022 Knut Sveidqvist; License: MIT (https://licenses.nuget.org/MIT). The minified file bundles mermaid's own dependencies; their notices stay in the comments of the file.
- Source: https://github.com/mermaid-js/mermaid

The browser draws the diagrams of Claude's answers with it (`securityLevel: 'strict'`).
