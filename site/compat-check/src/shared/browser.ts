import type { CheckStatus } from "./checks.js";

export type BrowserFamily = "chrome" | "edge" | "brave" | "opera" | "firefox" | "safari" | "other";
export type OsFamily = "windows" | "macos" | "linux" | "chromeos" | "android" | "ios" | "other";

export const MIN_CHROMIUM = 123;
const CHROMIUM: readonly BrowserFamily[] = ["chrome", "edge", "brave", "opera"];

const BRANDS: readonly [string, BrowserFamily][] = [
	["Microsoft Edge", "edge"], ["Opera", "opera"], ["Brave", "brave"], ["Google Chrome", "chrome"], ["Chromium", "chrome"],
];

function major(v: string | undefined): number | null {
	const n = v === undefined ? NaN : parseInt(v, 10);
	return Number.isFinite(n) ? n : null;
}

function token(ua: string, re: RegExp): string | undefined {
	return re.exec(ua)?.[1];
}

export function detectBrowser(
	brands: readonly { brand: string; version: string }[] | null,
	userAgent: string,
	isBrave: boolean,
): { family: BrowserFamily; major: number | null } {
	const fromUa = detectFromUa(userAgent);
	if (brands) {
		for (const [name, family] of BRANDS) {
			const b = brands.find((x) => x.brand === name);
			if (b) return { family: isBrave ? "brave" : family, major: major(b.version) };
		}
	}
	return isBrave ? { family: "brave", major: fromUa.major } : fromUa;
}

function detectFromUa(ua: string): { family: BrowserFamily; major: number | null } {
	const rules: [RegExp, BrowserFamily][] = [
		[/Edg\/(\d+)/, "edge"], [/OPR\/(\d+)/, "opera"], [/Firefox\/(\d+)/, "firefox"], [/Chrome\/(\d+)/, "chrome"],
	];
	for (const [re, family] of rules) {
		const v = token(ua, re);
		if (v !== undefined) return { family, major: major(v) };
	}
	const safari = token(ua, /Version\/(\d+)[\d.]* .*Safari\//);
	return safari === undefined ? { family: "other", major: null } : { family: "safari", major: major(safari) };
}

export function detectOs(platform: string | null, userAgent: string): OsFamily {
	if (platform) {
		const p = platform.toLowerCase();
		if (p === "windows") return "windows";
		if (p === "macos") return "macos";
		if (p === "linux") return "linux";
		if (p === "chrome os" || p === "chromeos") return "chromeos";
		if (p === "android") return "android";
		if (p === "ios") return "ios";
	}
	if (/Windows NT/.test(userAgent)) return "windows";
	if (/iPhone|iPad/.test(userAgent)) return "ios";
	if (/Mac OS X/.test(userAgent)) return "macos";
	if (/CrOS/.test(userAgent)) return "chromeos";
	if (/Android/.test(userAgent)) return "android";
	if (/Linux/.test(userAgent)) return "linux";
	return "other";
}

export function browserStatus(family: BrowserFamily, majorVersion: number | null): CheckStatus {
	if (!CHROMIUM.includes(family)) return "fail";
	return majorVersion !== null && majorVersion >= MIN_CHROMIUM ? "pass" : "warn";
}
