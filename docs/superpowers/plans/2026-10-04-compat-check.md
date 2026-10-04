# Compatibility Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Cloudflare-hosted page that checks whether a browser can run ai-chrome-proxy and, on request, stores an anonymous report in D1.

**Architecture:** `site/compat-check/` is a standalone Cloudflare Worker with static assets. Pure TypeScript modules (`src/shared/`) hold detection, verdict and report validation and are used by both the page and the Worker. The page (`src/page/`) is compiled by `tsc` into `public/js/`; the Worker (`src/worker/`) is bundled by wrangler. Tests run with `node --test` on `tsc` output.

**Tech Stack:** TypeScript 7 (strict), Cloudflare Workers + static assets + D1, wrangler 4, `node:test`.

**Spec:** [docs/superpowers/specs/2026-10-04-compat-check-design.md](../specs/2026-10-04-compat-check-design.md).

## Global Constraints

- Everything under `site/compat-check/`; nothing in the .NET solution changes except CI and docs. No personal domain, account id or database id in committed files (`wrangler.jsonc` gitignored, `wrangler.example.jsonc` committed with `database_id` `"<your-d1-database-id>"`).
- UTF-8 without BOM, LF line endings for this folder is fine (`* text=auto`); tabs for indentation; English only.
- TypeScript strict, no `any`; no hand-written `.js`; compiled output (`public/js/`, `build/`) and `node_modules/`, `.wrangler/` gitignored. Latest stable: `wrangler` 4.147.0, `typescript` 7.0.2, `@cloudflare/workers-types` latest; `package-lock.json` committed.
- Check ids (exact, in order): `secure-context`, `browser`, `wasm`, `worker`, `indexeddb`, `websocket`, `folder-api`, `folder-read`, `folder-write`, `rename`. Statuses: `pass`, `warn`, `fail`, `skipped`. Verdicts: `ready`, `limited`, `unsupported` (shown as **Ready**, **Ready with limitations**, **Not supported**).
- Browser families: `chrome`, `edge`, `brave`, `opera`, `firefox`, `safari`, `other`; Chromium ≥ 123 pass, older Chromium warn, non-Chromium fail. OS families: `windows`, `macos`, `linux`, `chromeos`, `android`, `ios`, `other`.
- Report body ≤ 4096 bytes; keys exactly `browser`, `browserMajor` (integer 1–999 or null), `os`, `verdict`, `checks` (array of `{ id, status, error? }`, each id at most once); `error` matches `^[A-Za-z]{1,40}$`. Report id: 8 chars from Crockford base32 `0123456789ABCDEFGHJKMNPQRSTVWXYZ` via `crypto.getRandomValues`. Day stored as UTC `YYYY-MM-DD`.
- WebSocket echo: `/api/ws`, closes after 10 messages or 30 s; page timeout 5 s.
- Probe file `.aicp-compat-<8 random hex>.tmp`, 16 bytes, removed afterwards; read check lists at most 100 entries and reads 64 bytes of one file.
- Page CSP: `default-src 'self'; connect-src 'self'; worker-src blob:; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`. No external requests.
- Commands (in `site/compat-check`): `npm ci`, `npm test` (= `tsc -p tsconfig.json && tsc -p tsconfig.worker.json --noEmit && node --test build/`), `npx wrangler dev` for manual runs. Never run `wrangler login`, `deploy` or `d1 create` — the owner does that.
- Commit only the task's files; no AI attribution.

---

### Task 1: Shared logic, project scaffold, CI

**Files:** `site/compat-check/package.json`, `package-lock.json`, `tsconfig.json` (page + shared + tests → `build/` for tests; page also emitted to `public/js/` via a second config or `outDir` choice — keep one source of compiled page JS), `tsconfig.worker.json` (worker + shared, `noEmit`, workers types), `.gitignore`; `src/shared/checks.ts` (types `CheckId`, `CheckStatus`, `CheckResult { id, status, error? }`, `CHECK_IDS`), `src/shared/browser.ts` (`detectBrowser(brands: readonly {brand: string; version: string}[] | null, userAgent: string, isBrave: boolean): { family, major: number | null }`, `detectOs(platform: string | null, userAgent: string)`, `browserStatus(family, major): CheckStatus`), `src/shared/verdict.ts` (`verdict(results): 'ready' | 'limited' | 'unsupported'`), `src/shared/report.ts` (`validateReport(body: string): Report | null` per the constraints, `newReportId(random: (bytes: Uint8Array) => void): string`); tests `src/test/*.test.ts`; `.github/workflows/ci.yml` new job `compat-check` (ubuntu-latest, `actions/setup-node` Node 22, `npm ci` + `npm test` in `site/compat-check`).
- [ ] Tests first (tables from the spec's Testing section) → fail → implement → pass.
- [ ] Commit `Compat check: shared detection, verdict and report validation`.

### Task 2: Worker and storage

**Files:** `src/worker/index.ts` (`fetch`: `POST /api/report` → `validateReport` → insert into `env.DB` → `{ id }`, 400 on invalid, 413 over 4 KB, 405 wrong method; `GET /api/ws` upgrade → echo via `WebSocketPair`, limits from the constraints; other `/api/*` → 404; everything else → `env.ASSETS.fetch`), `migrations/0001_reports.sql`, `wrangler.example.jsonc` (name `aicp-compat`, `main`, `compatibility_date` today, `assets { directory: "./public", binding: "ASSETS", run_worker_first: ["/api/*"] }`, `d1_databases` binding `DB`, commented `routes` example with `check.example.com`), `public/_headers` (CSP and `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`); Worker request-handling logic split so the routing/validation path is testable without Workers runtime (e.g. `handleReport(body, insert, now, random)` in `src/worker/report-handler.ts`, tested with a fake insert).
- [ ] Tests → implement → `npm test` green; `npx wrangler dev` with a local D1 (`npx wrangler d1 migrations apply aicp-compat --local`) answers a POST with an id (use a scratch copy of the example config as `wrangler.jsonc`, not committed).
- [ ] Commit `Compat check: Worker API and D1 schema`.

### Task 3: Page and docs

**Files:** `public/index.html`, `public/style.css`, `src/page/main.ts` (runs checks 1–7 on load, shows each row with status and explanation, **Pick a test folder** runs 8–10, verdict banner, **Copy result**, **Send anonymous report** → shows id or error; privacy note next to the button), `src/page/probes.ts` (one async function per check returning `CheckResult`; never throws; errors map to a DOMException name); `docs/compat-check.md` (what it checks, privacy, deploy: `npx wrangler login`, `npx wrangler d1 create aicp-compat`, copy example → `wrangler.jsonc` + id, `npx wrangler d1 migrations apply aicp-compat --remote`, `npm run deploy`, optional custom domain, rate-limiting rule on `/api/report`, reading reports with `wrangler d1 execute`), README line, CLAUDE.md line (site uses Node; product does not). Manual check with `wrangler dev` in Chrome (all rows, folder flow, report sent, row in local D1, no CSP errors in the console), dark/light screenshots to the scratchpad, looked at.
- [ ] Commit `Compat check: page and deploy guide`.
