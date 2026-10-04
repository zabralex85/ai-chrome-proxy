# Compatibility check page

Date: 2026-10-04. Owner's request: a page a prospective user opens in the browser of their locked-down (RDP) machine, before installing anything, to learn whether ai-chrome-proxy can work there; the results are kept in Cloudflare.

## Goal

One public page, hosted on Cloudflare, that runs the checks the app depends on, shows a verdict with plain explanations, and — only when the user clicks **Send anonymous report** — stores the result without personal data and shows a short report id the user can quote to support.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Hosting | A Cloudflare Worker with static assets (`site/compat-check/`): `public/` holds the page; the Worker answers `/api/*` and a D1 database `aicp-compat` stores reports. Default address `*.workers.dev`; a custom domain is set in the deployer's own `wrangler.jsonc` (gitignored; `wrangler.example.jsonc` is committed). | Free tier, one deploy unit, no personal domain or database id in the repo. |
| Tooling | Node is used for this site only (`package.json`: `wrangler`, `typescript`, `@cloudflare/workers-types`). The page script is strict TypeScript compiled by `tsc` into `public/js/` (gitignored); the Worker is bundled by wrangler. Tests use `node --test` on the compiled output. CI gets a `compat-check` job (`npm ci`, `npm test`). The .NET product still needs no Node. | Deploying a Worker needs wrangler (Node) anyway; separate from the product build. |
| Checks | In order; each ends **pass**, **warn**, **fail** or **skipped** with a one-line explanation: 1 secure context (`isSecureContext`); 2 browser family and major version (Chrome, Edge, Brave via `navigator.brave`, Opera, Firefox, Safari, Other; from `userAgentData` brands, else the UA string) — Chromium ≥ 123 pass, older Chromium warn, non-Chromium fail; 3 WebAssembly (instantiate an 8-byte empty module) — fail if blocked (e.g. Edge enhanced security mode), the app is Blazor WebAssembly; 4 Web Worker from a `blob:` URL echoes a message — warn if blocked (code viewer); 5 IndexedDB open + delete a test database — warn if blocked; 6 WebSocket echo through the Worker (`/api/ws`) within 5 s — warn if blocked (the app falls back to slower transports); 7 `showDirectoryPicker` present — fail if missing, with the Brave flag hint (`brave://flags/#file-system-access-api`) on Brave and the `chrome://policy` hint otherwise; 8 **Pick a test folder** (user click) — read: list up to 100 entries and read the first 64 bytes of one file — fail if refused or blocked; 9 write: request `readwrite`, create `.aicp-compat-<random>.tmp`, write 16 bytes, read them back, remove it — warn if refused (the app then works one way: the server cannot write back); 10 rename support (`move` on `FileSystemHandle`) — warn if missing (folder rename unavailable). Checks 8–10 run only after the click; before that they are skipped. | The app's real dependencies; every check is harmless and leaves nothing behind. |
| Verdict | **Ready** (no fail, no warn), **Ready with limitations** (warns only; list them), **Not supported** (any fail; list the blockers). Computed by a pure function over the check results. | One clear answer. |
| Privacy | Nothing is sent without the click. The report holds only: day (`YYYY-MM-DD`), browser family, major version, OS family (Windows, macOS, Linux, ChromeOS, Android, iOS, Other), verdict, and per check its id, status and an error name (a `DOMException` name matching `^[A-Za-z]{1,40}$`, else `Error`). No IP, no user agent string, no folder or file names, no cookies. The page says this next to the button. | No personal data stored. |
| API | `POST /api/report` — JSON body ≤ 4 KB, strict validation (known keys only, enums, known check ids, each at most once), else `400`; inserts a row and returns `{ "id": "<8 chars, Crockford base32>" }`. `GET /api/ws` — WebSocket echo (upgrade only, closes after 10 messages or 30 s). Any other `/api/*` → `404`. No read API: the owner queries D1 with `wrangler d1 execute`. | Small surface. |
| Storage | D1 table `reports(id TEXT PRIMARY KEY, day TEXT NOT NULL, browser TEXT NOT NULL, browser_major INTEGER, os TEXT NOT NULL, verdict TEXT NOT NULL, checks TEXT NOT NULL)` created by `migrations/0001_reports.sql`. | Simple to query. |
| Page | English, single page, light/dark via `prefers-color-scheme`, works at phone width, no external requests (no fonts, no CDNs), CSP `default-src 'self'; connect-src 'self'; worker-src blob:; script-src 'self' 'wasm-unsafe-eval'` set by the Worker on the page (`_headers` in `public/`). Shows a **Copy result** button (plain text summary) and, after sending, the report id. | Works behind strict proxies; same CSP family as the app. |

## Testing

`node --test` (CI): verdict table (all pass → Ready; a warn → limitations; a fail beats warns); browser and OS detection from sample brands/UA strings (Chrome, Edge, Brave, Opera, Firefox, Safari, old Chrome); report validation (valid accepted; unknown key, unknown check id, duplicate check, bad status, long or odd error name, body over 4 KB, wrong types → rejected); id format. Manual: `npx wrangler dev` locally in Chrome, Brave without the flag, Firefox; the deployed page from the owner's RDP machine; `wrangler d1 execute aicp-compat --remote --command "select * from reports"` shows the row.

## Known limitations

Policies the page cannot see (download blocks, clipboard, proxy rules for the user's own subdomain) are not checked. `/api/report` is limited per IP by the Workers rate limiting binding (10 per minute, approximate, per location); the IP is the limiter key only and is not stored.

## Done when

- The page runs all checks in Chrome and reports Not supported clearly in Firefox and in Brave without the flag.
- A sent report lands in D1 with only the listed fields; the id is shown.
- CI green (the new job and the existing gate); `docs/compat-check.md` explains deploy (login, D1 create, migration, deploy, custom domain, rate-limit rule) and how to read the reports.
