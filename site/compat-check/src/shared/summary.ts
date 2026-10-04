import type { BrowserFamily, OsFamily } from "./browser.js";
import type { CheckResult, CheckStatus } from "./checks.js";
import type { Report } from "./report.js";
import { verdict, type Verdict } from "./verdict.js";

export const VERDICT_LABELS: Record<Verdict, string> = {
	ready: "Ready",
	limited: "Ready with limitations",
	unsupported: "Not supported",
};

export const STATUS_LABELS: Record<CheckStatus, string> = {
	pass: "Pass",
	warn: "Warning",
	fail: "Failed",
	skipped: "Not run",
};

export const BROWSER_NAMES: Record<BrowserFamily, string> = {
	chrome: "Chrome", edge: "Edge", brave: "Brave", opera: "Opera", firefox: "Firefox", safari: "Safari", other: "Other browser",
};

export const OS_NAMES: Record<OsFamily, string> = {
	windows: "Windows", macos: "macOS", linux: "Linux", chromeos: "ChromeOS", android: "Android", ios: "iOS", other: "Other OS",
};

export interface Environment {
	family: BrowserFamily;
	major: number | null;
	os: OsFamily;
}

/** The error name a report may carry: a DOMException-style name, anything else becomes "Error". */
export function errorName(e: unknown): string {
	const name = typeof e === "object" && e !== null && "name" in e ? e.name : undefined;
	return typeof name === "string" && /^[A-Za-z]{1,40}$/.test(name) ? name : "Error";
}

export function browserLabel(env: Environment): string {
	const version = env.major === null ? "" : ` ${env.major}`;
	return `${BROWSER_NAMES[env.family]}${version} on ${OS_NAMES[env.os]}`;
}

export function buildReport(env: Environment, results: readonly CheckResult[]): Report {
	const major = env.major !== null && env.major >= 1 && env.major <= 999 ? env.major : null;
	return {
		browser: env.family,
		browserMajor: major,
		os: env.os,
		verdict: verdict(results),
		checks: results.map((r) => (r.error === undefined ? { id: r.id, status: r.status } : { id: r.id, status: r.status, error: r.error })),
	};
}

export function summaryText(env: Environment, results: readonly CheckResult[], day: string): string {
	const lines = [
		`ai-chrome-proxy compatibility check, ${day}`,
		`Verdict: ${VERDICT_LABELS[verdict(results)]}`,
		`Browser: ${browserLabel(env)}`,
		"",
		...results.map((r) => `${r.id}: ${STATUS_LABELS[r.status]}${r.error === undefined ? "" : ` (${r.error})`}`),
	];
	return lines.join("\n") + "\n";
}
