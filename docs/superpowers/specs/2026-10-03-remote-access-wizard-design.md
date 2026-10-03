# Sub-project 2b — Remote access wizard: Cloudflare Tunnel + Access in one step

Date: 2026-10-03. Parent: [architecture](2026-10-02-architecture-design.md). Builds on [2a Windows host](2026-10-02-windows-host-design.md).

## Goal

After `Setup.exe`, a developer publishes the home server as `https://<sub>.<their zone>` behind Cloudflare Tunnel + Cloudflare Access without touching the Zero Trust dashboard (except enabling Zero Trust once) and without installing `cloudflared` separately. The tray's wizard takes one Cloudflare API token, a zone, a subdomain and the allowed email addresses, then creates or reuses the tunnel, DNS record, Access policy and application, and writes the Server settings. The Server runs `cloudflared` itself, under the same account, so the tunnel is up after power-on without anyone logging in.

The manual path in `docs/setup/cloudflare.md` stays for people who prefer the dashboard.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Cloudflare automation | Cloudflare REST API (`https://api.cloudflare.com/client/v4`) with a user API token, remotely-managed tunnel (`config_src: cloudflare`). | `cloudflared tunnel login` gives a zone certificate only: it can create tunnels and DNS but not Access applications. One token covers everything. |
| Running `cloudflared` | Child process of the Server (`CloudflaredSupervisor`, a hosted service), started when `Tunnel:Token` is set. Token passed in the `TUNNEL_TOKEN` environment variable of the child, never on a command line. | No second Windows service; `cloudflared service install` would run as LocalSystem from a binary in the user's writable profile (privilege escalation). One process tree, one log. |
| `cloudflared` binary | Bundled in the release: `server\cloudflared.exe`, pinned version `2026.9.3`, SHA256 `f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2` verified in `release.yml`. Override `Tunnel:CloudflaredPath`; fallback `cloudflared` on `PATH` (dev, `winget install Cloudflare.cloudflared`). | One setup installs everything. Updated by bumping the pin; `--no-autoupdate` always. |
| Where the API token lives | Memory of the wizard window only. Never written to disk, never logged, sent only to `api.cloudflare.com`. | It can edit DNS and Access for the whole account. |
| Where the tunnel token lives | `<DataDir>\appsettings.json` → `Tunnel:Token`. The data directory has the protected DACL from 2a (SYSTEM, Administrators, the account). The Settings window keeps the key untouched and never shows it. | Same trust boundary as the rest of the Server config. |
| Zero Trust organisation | Read with `GET /accounts/{id}/access/organizations` (`auth_domain` = team domain). If Access is not enabled the wizard stops with "Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry". No creation via API. | Onboarding needs a plan choice in the dashboard; API creation on fresh accounts is not reliable. |
| Idempotency | Every step finds before it creates: tunnel by name, DNS record by name, Access policy by name, Access application by domain. Re-running the wizard (other emails, other subdomain, lost settings file) converges instead of duplicating. | The wizard will be re-run. |
| Conflicts | An existing DNS record for the host name that is not a CNAME to this tunnel → stop with "`<host>` already has a DNS record; choose another subdomain or delete it". Never overwrite foreign records. | Don't break the user's existing site. |
| Cleanup | Out of scope. Docs explain deleting the tunnel, DNS record and Access app in the dashboard. | YAGNI; rare. |
| `claude` CLI check | Out of scope (Claude chat sub-project). | Not needed to reach the UI. |

## 1. Cloudflare API client (`src/AiChromeProxy.Infrastructure/Cloudflare`)

The tray uses it; the Server does not. `HttpClient` injected (tests pass a fake `HttpMessageHandler`; no real network in tests).

- `CloudflareApi` — thin typed calls, bearer token, `{ success, errors[{code,message}], result, result_info }` envelope; `success: false` or a non-2xx status → `CloudflareApiException` carrying the first error's code and message (never the token). HTTP 429 → wait `Retry-After` (cap 60 s) once, then fail. Paginated lists follow `result_info.total_pages` (`per_page` 50).
  - `VerifyTokenAsync` → `GET /user/tokens/verify` (`result.status` must be `active`).
  - `ListZonesAsync` → `GET /zones?status=active` → `{ id, name, account { id, name } }`. Accounts are taken from the zones (no "Account Settings Read" permission needed).
  - Tunnels: `GET /accounts/{a}/cfd_tunnel?name=&is_deleted=false`, `POST /accounts/{a}/cfd_tunnel {name, config_src:"cloudflare"}`, `GET /accounts/{a}/cfd_tunnel/{id}/token`, `PUT /accounts/{a}/cfd_tunnel/{id}/configurations {config:{ingress:[{hostname, service:"http://127.0.0.1:<port>"},{service:"http_status:404"}]}}`.
  - DNS: `GET /zones/{z}/dns_records?name=<fqdn>`, `POST /zones/{z}/dns_records {type:"CNAME", name:<fqdn>, content:"<tunnelId>.cfargotunnel.com", proxied:true, ttl:1}`, `PATCH /zones/{z}/dns_records/{id} {proxied:true}` when our CNAME exists unproxied.
  - Access: `GET /accounts/{a}/access/organizations` (`auth_domain`); policies `GET/POST/PUT /accounts/{a}/access/policies[/{id}]` `{name, decision:"allow", include:[{email:{email}}…]}`; apps `GET/POST/PUT /accounts/{a}/access/apps[/{id}]` `{name, type:"self_hosted", domain:<fqdn>, session_duration:"24h", policies:[{id, precedence:1}]}`, `aud` read from the result.
- `RemoteAccessProvisioner.ProvisionAsync(RemoteAccessRequest, IProgress<string>, ct)` → `RemoteAccessResult { TeamDomain, Audience, PublicHost, TunnelToken }`. Steps, each reported as one progress line:
  1. Team domain from the organisation (stop if Access is not enabled — error code `access.api.error.not_enabled` or 4xx on that call).
  2. Tunnel `ai-chrome-proxy-<machine name, lower-case>`: reuse or create; fetch its token.
  3. Ingress: `<fqdn>` → `http://127.0.0.1:<Server:Port>`, then the 404 catch-all (full replace — the tunnel is ours).
  4. DNS CNAME (create / keep / make proxied / conflict error as above).
  5. Access policy `AI Chrome Proxy — <fqdn>`: create or replace its `include` with the given emails.
  6. Access application for `<fqdn>`: create or update (name `AI Chrome Proxy`, the policy above); read `aud`.
- Input validation (pure, tested): subdomain is one DNS label (`[a-z0-9-]`, 1–63, no leading/trailing `-`); at least one email, each `local@domain` with a dot in the domain; the resulting FQDN passes `HostName` validation.
- Required token permissions, shown in the wizard and the docs: Account — *Cloudflare Tunnel: Edit*, *Access: Apps and Policies: Edit*, *Access: Organizations, Identity Providers, and Groups: Read*; Zone — *DNS: Edit*, *Zone: Read*. The wizard's "Create token" button opens `https://dash.cloudflare.com/profile/api-tokens`.

## 2. `cloudflared` in the Server (`src/AiChromeProxy.Server/Hosting`)

- `TunnelOptions` (Infrastructure/Hosting, section `Tunnel`): `Token` (secret), `CloudflaredPath` (optional). Shared with the tray (key names).
- `CloudflaredSupervisor : BackgroundService`:
  - No token → logs "Cloudflare Tunnel not configured" once and returns.
  - Resolves the binary: `CloudflaredPath` → `<AppContext.BaseDirectory>\cloudflared.exe` if present → `cloudflared` (PATH).
  - Starts `cloudflared tunnel --no-autoupdate run` with `TUNNEL_TOKEN` in the child environment; stdout/stderr lines go to the Server log (`cloudflared: {Line}`, Information), so the tray's Logs window shows them.
  - Restarts on exit with backoff 1 s, 2 s, 4 s … capped at 60 s; the backoff resets after a run that lasted ≥ 5 min. Start failures (binary missing) are logged as errors and retried the same way.
  - On shutdown kills the process tree. On Windows the child is assigned to a Job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so a crashed Server never leaves an orphan `cloudflared` holding the tunnel.
  - Testability: process start behind a small seam (`ICloudflaredProcess` / factory); backoff, token-absent, restart and shutdown logic unit-tested with a fake and `FakeTimeProvider`. The real process + Job object live in a thin `[ExcludeFromCodeCoverage]` class.
- The tunnel token never appears in logs, exceptions or the command line.

## 3. Tray wizard (`src/AiChromeProxy.Tray`)

- Menu item **Set up remote access…** (above Settings…). Also opened automatically at tray start when the settings file has no `Server:PublicHost` (first run).
- `RemoteAccessWindow` + `RemoteAccessViewModel`, one window, three stages:
  1. **Token** — password box, "Create token" link, permission checklist, **Continue** → verifies the token and loads zones.
  2. **Details** — zone (drop-down), subdomain (default `code`), allowed emails (comma or newline separated), the resulting `https://<sub>.<zone>` shown live; **Set up** → validation errors inline.
  3. **Progress** — one line per provisioning step; on failure the error and **Back**; on success the settings are written and the window offers **Install service…** (when not installed, the existing elevated flow) or **Restart service** (when running), then **Open** (`https://<fqdn>/`).
- Writing settings: `TeamDomain`, `Audience`, `PublicHost`, `Tunnel:Token` merged into `<DataDir>\appsettings.json` with the same atomic write the Settings window uses — extracted into one shared helper (`SettingsFile`), so both windows write the same way and keep unknown keys.
- The view model is tested with the API client over a fake handler; no seam beyond `HttpClient`.

## 4. Release

`release.yml`: after publishing the Server, download `https://github.com/cloudflare/cloudflared/releases/download/2026.9.3/cloudflared-windows-amd64.exe`, verify the pinned SHA256 (fail the job on mismatch), copy to `publish/server/cloudflared.exe`. Version and hash are job-level `env` values. `THIRD-PARTY-NOTICES.md` names cloudflared (Apache-2.0) and its source.

## 5. Documentation

- `docs/windows-host.md`: "Remote access" section — create the token (permissions list), run the wizard, what it creates, re-running it, removing the resources in the dashboard; the tunnel runs inside the service (no `cloudflared` service needed; if one was installed by the manual guide, uninstall it with `cloudflared service uninstall`).
- `docs/setup/cloudflare.md`: points to the wizard as the default, keeps the manual steps as the alternative.
- Manual acceptance checklist additions: wizard on a fresh zone; re-run with another email (policy updated, no duplicates); subdomain that already has a foreign record (refused); reboot without login → tunnel up; kill the Server process → no orphan `cloudflared`; Logs window shows `cloudflared:` lines.

## 6. Testing

xunit (CI), no network:
- API client: envelope parsing, error mapping (`success:false`, non-2xx, 429 + `Retry-After`), pagination, bearer header, token never in exception messages.
- Provisioner: fresh account (everything created, request order and bodies), second run (everything reused, policy updated), DNS conflict, CNAME unproxied → patched, Access not enabled → clear error.
- Input validation (subdomain, emails, FQDN).
- Supervisor: no token → never starts; exit → restart with growing backoff capped at 60 s; long run resets backoff; shutdown kills; token only in the child environment.
- Settings helper: merge keeps unknown keys; atomic write (existing tests move to the helper).
- Wizard view model: stage transitions, validation messages, success writes the four values.
- Architecture rules unchanged (Cloudflare client in Infrastructure; Tray → Domain + Infrastructure).

## Also in this sub-project (deferred minors from 2a)

- `ServiceSetup.PrepareDataDirectory`: owner lookup injectable, with a negative test that a foreign-owned entry refuses install.
- Server data-directory tests leave folders in `%TEMP%\aicp-tests` (a log file still open at dispose) — fix the leak.

## Done when

- Wizard provisions a real zone end to end (manual checklist on the home server, e.g. the developer's own zone).
- Release package contains `server\cloudflared.exe` with the verified hash.
- CI green; xunit gate ≥ 85%.

## Out of scope

- Removing Cloudflare resources from the tray.
- Creating the Zero Trust organisation via API.
- Identity providers other than Cloudflare's one-time PIN (email) default.
- macOS/Linux supervisor specifics (the supervisor is cross-platform except the Job object).
