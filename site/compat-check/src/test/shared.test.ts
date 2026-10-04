import { test } from "node:test";
import assert from "node:assert/strict";
import { CHECK_IDS, type CheckResult, type CheckStatus } from "../shared/checks.js";
import { browserStatus, detectBrowser, detectOs } from "../shared/browser.js";
import { verdict } from "../shared/verdict.js";
import { newReportId, validateReport } from "../shared/report.js";

const res = (...s: CheckStatus[]): CheckResult[] => s.map((status, i) => ({ id: CHECK_IDS[i]!, status }));

test("verdict", () => {
	assert.equal(verdict(res("pass", "pass", "skipped")), "ready");
	assert.equal(verdict(res("pass", "warn")), "limited");
	assert.equal(verdict(res("warn", "fail", "pass")), "unsupported");
	assert.equal(verdict([]), "ready");
});

const chromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
const b = (...names: string[]) => names.map((brand) => ({ brand, version: "130" }));

test("browser from brands", () => {
	assert.deepEqual(detectBrowser(b("Chromium", "Google Chrome", "Not A Brand"), chromeUa, false), { family: "chrome", major: 130 });
	assert.deepEqual(detectBrowser(b("Chromium", "Microsoft Edge"), chromeUa, false), { family: "edge", major: 130 });
	assert.deepEqual(detectBrowser(b("Chromium", "Opera"), chromeUa, false), { family: "opera", major: 130 });
	assert.deepEqual(detectBrowser(b("Chromium", "Brave"), chromeUa, false), { family: "brave", major: 130 });
	assert.deepEqual(detectBrowser(b("Chromium"), chromeUa, false), { family: "chrome", major: 130 });
	assert.equal(detectBrowser(b("Chromium", "Google Chrome"), chromeUa, true).family, "brave");
});

test("browser from UA", () => {
	assert.deepEqual(detectBrowser(null, chromeUa, false), { family: "chrome", major: 124 });
	assert.deepEqual(detectBrowser([], chromeUa + " Edg/125.0.1", false), { family: "edge", major: 125 });
	assert.deepEqual(detectBrowser(null, chromeUa + " OPR/110.0.0.0", false), { family: "opera", major: 110 });
	assert.deepEqual(detectBrowser(null, "Mozilla/5.0 (X11; Linux x86_64; rv:126.0) Gecko/20100101 Firefox/126.0", false), { family: "firefox", major: 126 });
	assert.deepEqual(detectBrowser(null, "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15", false), { family: "safari", major: 17 });
	assert.deepEqual(detectBrowser(null, chromeUa, true), { family: "brave", major: 124 });
	assert.deepEqual(detectBrowser(null, "curl/8", false), { family: "other", major: null });
});

test("browser status", () => {
	assert.equal(browserStatus("chrome", 123), "pass");
	assert.equal(browserStatus("edge", 122), "warn");
	assert.equal(browserStatus("brave", null), "warn");
	assert.equal(browserStatus("firefox", 130), "fail");
	assert.equal(browserStatus("safari", 17), "fail");
	assert.equal(browserStatus("other", null), "fail");
});

test("os", () => {
	assert.equal(detectOs("Windows", ""), "windows");
	assert.equal(detectOs("macOS", ""), "macos");
	assert.equal(detectOs("Chrome OS", ""), "chromeos");
	assert.equal(detectOs("ChromeOS", ""), "chromeos");
	assert.equal(detectOs("Android", ""), "android");
	assert.equal(detectOs(null, chromeUa), "windows");
	assert.equal(detectOs(null, "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)"), "macos");
	assert.equal(detectOs(null, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)"), "ios");
	assert.equal(detectOs(null, "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0)"), "chromeos");
	assert.equal(detectOs(null, "Mozilla/5.0 (Linux; Android 14; Pixel 8)"), "android");
	assert.equal(detectOs(null, "Mozilla/5.0 (X11; Linux x86_64)"), "linux");
	assert.equal(detectOs("Plan9", "x"), "other");
});

const valid = () => ({
	browser: "chrome", browserMajor: 124, os: "windows", verdict: "limited",
	checks: [{ id: "wasm", status: "pass" }, { id: "worker", status: "warn", error: "SecurityError" }],
});
const v = (o: unknown) => validateReport(JSON.stringify(o));
const mut = (f: (r: Record<string, unknown>) => void) => { const r = valid() as Record<string, unknown>; f(r); return v(r); };

test("report accepts valid", () => {
	assert.deepEqual(v(valid()), valid());
	assert.notEqual(mut((r) => { r["browserMajor"] = null; }), null);
	assert.notEqual(mut((r) => { r["checks"] = []; }), null);
});

test("report rejects extra and missing keys", () => {
	assert.equal(mut((r) => { r["extra"] = 1; }), null);
	assert.equal(mut((r) => { delete r["os"]; }), null);
	assert.equal(mut((r) => { (r["checks"] as unknown[])[0] = { id: "wasm", status: "pass", x: 1 }; }), null);
});

test("report rejects bad values", () => {
	assert.equal(validateReport("{"), null);
	assert.equal(validateReport("[]"), null);
	assert.equal(validateReport("null"), null);
	assert.equal(mut((r) => { r["browser"] = "ie"; }), null);
	assert.equal(mut((r) => { r["os"] = 5; }), null);
	assert.equal(mut((r) => { r["verdict"] = "ok"; }), null);
	assert.equal(mut((r) => { r["browserMajor"] = 0; }), null);
	assert.equal(mut((r) => { r["browserMajor"] = 1000; }), null);
	assert.equal(mut((r) => { r["browserMajor"] = 1.5; }), null);
	assert.equal(mut((r) => { r["browserMajor"] = "1"; }), null);
	assert.equal(mut((r) => { r["checks"] = "x"; }), null);
	assert.equal(mut((r) => { r["checks"] = [{ id: "nope", status: "pass" }]; }), null);
	assert.equal(mut((r) => { r["checks"] = [{ id: "wasm", status: "great" }]; }), null);
	assert.equal(mut((r) => { r["checks"] = [{ id: "wasm", status: "pass" }, { id: "wasm", status: "fail" }]; }), null);
	for (const error of ["", "a".repeat(41), "Not Found", "Err1", 5, null]) {
		assert.equal(mut((r) => { r["checks"] = [{ id: "wasm", status: "fail", error }]; }), null, String(error));
	}
});

test("report size limit is in bytes", () => {
	const ok = JSON.stringify(valid());
	assert.notEqual(validateReport(ok), null);
	assert.equal(validateReport(ok + " ".repeat(4096)), null);
	const pad = (n: number) => ok.slice(0, -1) + " ".repeat(n - ok.length) + "}";
	assert.equal(new TextEncoder().encode(pad(4096)).length, 4096);
	assert.notEqual(validateReport(pad(4096)), null);
	// 2100 chars, 4200 bytes: rejected by size alone (padding stays valid JSON)
	assert.equal(validateReport(ok.slice(0, -1) + " ".repeat(10) + "}" + "é".repeat(2100)), null);
});

test("report id", () => {
	const id = newReportId((bytes) => bytes.set([0, 1, 255, 32, 17, 18, 19, 20]));
	assert.match(id, /^[0-9A-HJKMNP-TV-Z]{8}$/);
	assert.equal(id, "01Z0HJKM");
});
