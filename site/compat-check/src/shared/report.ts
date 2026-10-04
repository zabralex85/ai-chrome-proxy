import { CHECK_IDS, type CheckId, type CheckResult, type CheckStatus } from "./checks.js";
import type { BrowserFamily, OsFamily } from "./browser.js";
import type { Verdict } from "./verdict.js";

export interface Report {
	browser: BrowserFamily;
	browserMajor: number | null;
	os: OsFamily;
	verdict: Verdict;
	checks: CheckResult[];
}

export const MAX_REPORT_BYTES = 4096;
const BROWSERS: readonly string[] = ["chrome", "edge", "brave", "opera", "firefox", "safari", "other"];
const OSES: readonly string[] = ["windows", "macos", "linux", "chromeos", "android", "ios", "other"];
const VERDICTS: readonly string[] = ["ready", "limited", "unsupported"];
const STATUSES: readonly string[] = ["pass", "warn", "fail", "skipped"];
const ERROR_RE = /^[A-Za-z]{1,40}$/;
const ID_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

type Obj = Record<string, unknown>;

function isObj(v: unknown): v is Obj {
	return typeof v === "object" && v !== null && !Array.isArray(v);
}

function onlyKeys(o: Obj, keys: readonly string[]): boolean {
	return Object.keys(o).every((k) => keys.includes(k));
}

function oneOf(list: readonly string[], v: unknown): v is string {
	return typeof v === "string" && list.includes(v);
}

function parseCheck(c: unknown): CheckResult | null {
	if (!isObj(c) || !onlyKeys(c, ["id", "status", "error"])) return null;
	if (!oneOf(CHECK_IDS, c["id"]) || !oneOf(STATUSES, c["status"])) return null;
	const id = c["id"] as CheckId;
	const status = c["status"] as CheckStatus;
	if (!("error" in c)) return { id, status };
	const e = c["error"];
	return typeof e === "string" && ERROR_RE.test(e) ? { id, status, error: e } : null;
}

export function validateReport(body: string): Report | null {
	if (new TextEncoder().encode(body).length > MAX_REPORT_BYTES) return null;
	let r: unknown;
	try {
		r = JSON.parse(body);
	} catch {
		return null;
	}
	if (!isObj(r) || Object.keys(r).length !== 5) return null;
	const { browser, browserMajor, os, verdict, checks } = r;
	if (!oneOf(BROWSERS, browser) || !oneOf(OSES, os) || !oneOf(VERDICTS, verdict)) return null;
	if (browserMajor !== null && !(Number.isInteger(browserMajor) && (browserMajor as number) >= 1 && (browserMajor as number) <= 999)) return null;
	if (!Array.isArray(checks) || checks.length > CHECK_IDS.length) return null;
	const parsed: CheckResult[] = [];
	for (const c of checks) {
		const p = parseCheck(c);
		if (!p || parsed.some((x) => x.id === p.id)) return null;
		parsed.push(p);
	}
	return {
		browser: browser as BrowserFamily,
		browserMajor: browserMajor as number | null,
		os: os as OsFamily,
		verdict: verdict as Verdict,
		checks: parsed,
	};
}

export function newReportId(random: (bytes: Uint8Array) => void): string {
	const bytes = new Uint8Array(8);
	random(bytes);
	let id = "";
	for (const b of bytes) id += ID_ALPHABET[b % 32];
	return id;
}
