# Compatibility check page

A standalone page that tells a prospective user whether ai-chrome-proxy can run in their browser, before they install anything. They open it in the browser of the machine they will work from (for example, over RDP); it runs the checks, shows a verdict and, only when they click **Send anonymous report**, stores the result in Cloudflare D1 and shows a short report id they can quote when asking for help.

Source: [`site/compat-check/`](../site/compat-check) — a Cloudflare Worker with static assets and a D1 database. Design: [spec](superpowers/specs/2026-10-04-compat-check-design.md). It uses Node (wrangler, `tsc`, `node --test`) for this site only; the product build still needs no Node.

## What it checks

| # | Check | Pass | Otherwise |
|---|---|---|---|
| 1 | Secure context (`isSecureContext`) | HTTPS or localhost | **fail**: folder access needs a secure context |
| 2 | Browser family and major version | Chromium (Chrome, Edge, Brave, Opera) 123+ | older Chromium **warn**; Firefox, Safari, other **fail** |
| 3 | WebAssembly (instantiate an empty module) | runs | **fail**: the app is Blazor WebAssembly (in Edge usually enhanced security mode — add an exception for the site) |
| 4 | Web Worker from a `blob:` URL | echoes a message within 5 s | **warn**: the code viewer is limited |
| 5 | IndexedDB (open and delete a test database) | works | **warn**: no remembered folder and no hash cache |
| 6 | WebSocket echo through `/api/ws` | echo within 5 s | **warn**: the app falls back to slower transports |
| 7 | `showDirectoryPicker` present | present | **fail**; on Brave: enable `brave://flags/#file-system-access-api` and relaunch; Chrome/Edge: check `chrome://policy` / `edge://policy` for File System Access policies (`DefaultFileSystemReadGuardSetting`, `DefaultFileSystemWriteGuardSetting`) or ask IT |
| 8 | Read a folder (after **Pick a test folder**) | lists up to 100 entries and reads 64 bytes of one file | **fail**: the app cannot sync |
| 9 | Write to the folder | `readwrite` granted; a 16-byte `.aicp-compat-<8 hex>.tmp` written, read back and removed | **warn**: the app works one way, the server cannot write back |
| 10 | Rename in place (`move` on `FileSystemFileHandle`) | available | **warn**: renaming a file copies it (up to 20 MB) |

Checks 1–7 run on load; 8 and 10 run when the user clicks **Pick a test folder** (cancelling the picker leaves them "Not run"), 9 on a separate **Test writing** click (the picker consumes the user activation that the write permission prompt needs). Verdict: **Ready** (all pass), **Ready with limitations** (warnings only, listed), **Not supported** (any failure, blockers listed). **Copy result** puts a plain-text summary on the clipboard (or shows it selected in a text box when the clipboard is blocked).

## Privacy

Nothing leaves the browser until **Send anonymous report** is clicked (once per page load). The report holds only: the day (`YYYY-MM-DD`, UTC), browser family, major version, OS family, the verdict and, per check, its id, status and an error name (a `DOMException` name such as `NotAllowedError`, else `Error`). No IP address, user agent string, file or folder names, or cookies are stored. The page makes no external requests (CSP `default-src 'self'`).

## Run locally

In `site/compat-check`:

```bash
npm ci
npm run build
cp wrangler.example.jsonc wrangler.jsonc     # any database_id works locally
npx wrangler d1 migrations apply aicp-compat --local
npx wrangler dev
```

Open the printed `http://localhost:8787/`. `npm test` compiles everything and runs the `node --test` suites (also run by CI).

## Deploy

Needs a Cloudflare account (the free plan is enough). In `site/compat-check`:

1. `npx wrangler login`
2. `npx wrangler d1 create aicp-compat` — copy `wrangler.example.jsonc` to `wrangler.jsonc` (gitignored) and put the printed id into `database_id`.
3. `npx wrangler d1 migrations apply aicp-compat --remote`
4. `npm run deploy` (= `npm run build && wrangler deploy`; or run `npm run build` and `npx wrangler deploy` yourself).

The page is then at `https://aicp-compat.<your-subdomain>.workers.dev/`.

### Custom domain (optional)

For a zone in the same Cloudflare account, uncomment `routes` in `wrangler.jsonc`:

```jsonc
"routes": [{ "pattern": "check.example.com", "custom_domain": true }]
```

and deploy again; Cloudflare creates the DNS record and certificate.

### Rate limiting

The Worker limits `POST /api/report` to 10 requests per minute per IP through the Workers rate limiting binding `REPORT_LIMITER` (`ratelimits` in `wrangler.example.jsonc`); over the limit it answers `429`. The IP is only the limiter key and is never stored. Without the binding the endpoint is unlimited. The limit is per Cloudflare location and approximate; for a hard limit add a WAF rate limiting rule on the zone as well.

## Reading reports

There is no read API; query D1 directly:

```bash
npx wrangler d1 execute aicp-compat --remote --command "select day, browser, browser_major, os, verdict from reports order by day desc limit 50"
npx wrangler d1 execute aicp-compat --remote --command "select * from reports where id = 'ABCD1234'"
```

`checks` holds the per-check JSON array exactly as sent.
