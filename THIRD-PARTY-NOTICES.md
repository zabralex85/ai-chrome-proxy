# Third-party notices

The Windows release (`AiChromeProxy-win-Setup.exe`) bundles the following third-party software, unmodified.

## cloudflared

- File: `server\cloudflared.exe` (`cloudflared-windows-amd64.exe`, version 2026.9.3, SHA256 `f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2`)
- Copyright: Cloudflare, Inc.
- License: Apache License 2.0 — full text in `licenses\cloudflared-LICENSE.txt` (next to this file in the package; https://github.com/cloudflare/cloudflared/blob/master/LICENSE)
- Source: https://github.com/cloudflare/cloudflared (tag `2026.9.3`)

The Server starts it as a child process to connect the Cloudflare Tunnel configured by the tray's **Set up remote access…** wizard.

## SQLite storage (Microsoft.Data.Sqlite)

- Files: `Microsoft.Data.Sqlite.dll`, `Microsoft.Data.Sqlite.Core.dll`, `SQLitePCLRaw.*.dll` and the native `e_sqlite3.dll` (in the Server folder), unmodified.
- Microsoft.Data.Sqlite 10.0.12 — Copyright Microsoft Corporation; License: MIT (https://licenses.nuget.org/MIT)
- SQLitePCLRaw 2.1.12 (`bundle_e_sqlite3`, `lib.e_sqlite3`, `provider.e_sqlite3`, `core`) — Copyright 2014-2024 SourceGear, LLC; License: Apache License 2.0 (https://licenses.nuget.org/Apache-2.0)
- SQLite (native library inside `e_sqlite3.dll`) — public domain (https://www.sqlite.org/copyright.html)

The Server keeps the agreed hash per synced file and the project settings in `aicp.db` in the data folder.
