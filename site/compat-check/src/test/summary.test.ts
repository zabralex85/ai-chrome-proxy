import { test } from "node:test";
import assert from "node:assert/strict";
import type { CheckResult } from "../shared/checks.js";
import { validateReport } from "../shared/report.js";
import { browserLabel, buildReport, errorName, summaryText, type Environment } from "../shared/summary.js";

const env: Environment = { family: "chrome", major: 130, os: "windows" };
const results: CheckResult[] = [
	{ id: "secure-context", status: "pass" },
	{ id: "worker", status: "warn", error: "SecurityError" },
	{ id: "folder-read", status: "skipped" },
];

test("error name", () => {
	assert.equal(errorName(new DOMException("x", "NotAllowedError")), "NotAllowedError");
	assert.equal(errorName(new TypeError("x")), "TypeError");
	assert.equal(errorName({ name: "Has Space" }), "Error");
	assert.equal(errorName({ name: "a".repeat(41) }), "Error");
	assert.equal(errorName("boom"), "Error");
	assert.equal(errorName(null), "Error");
});

test("browser label", () => {
	assert.equal(browserLabel(env), "Chrome 130 on Windows");
	assert.equal(browserLabel({ family: "other", major: null, os: "other" }), "Other browser on Other OS");
});

test("report from results passes validation", () => {
	const r = buildReport(env, results);
	assert.deepEqual(r, {
		browser: "chrome", browserMajor: 130, os: "windows", verdict: "limited",
		checks: [{ id: "secure-context", status: "pass" }, { id: "worker", status: "warn", error: "SecurityError" }, { id: "folder-read", status: "skipped" }],
	});
	assert.deepEqual(validateReport(JSON.stringify(r)), r);
	assert.equal(buildReport({ ...env, major: 1000 }, results).browserMajor, null);
	assert.equal(buildReport({ ...env, major: 0 }, []).verdict, "ready");
});

test("summary text", () => {
	assert.equal(
		summaryText(env, results, "2026-10-04"),
		"ai-chrome-proxy compatibility check, 2026-10-04\n"
		+ "Verdict: Ready with limitations\n"
		+ "Browser: Chrome 130 on Windows\n"
		+ "\n"
		+ "secure-context: Pass\n"
		+ "worker: Warning (SecurityError)\n"
		+ "folder-read: Not run\n",
	);
});
