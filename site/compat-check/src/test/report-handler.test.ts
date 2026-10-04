import assert from "node:assert/strict";
import { test } from "node:test";
import { handleReport, type ReportRow } from "../shared/report-handler.js";

const rnd = (b: Uint8Array): void => { b.fill(1); };
const now = new Date("2026-10-04T23:59:59Z");
const good = JSON.stringify({ browser: "chrome", browserMajor: 130, os: "windows", verdict: "ready", checks: [{ id: "wasm", status: "pass" }] });

test("valid report is inserted and id returned", async () => {
	const rows: ReportRow[] = [];
	const r = await handleReport(good, async (x) => { rows.push(x); }, now, rnd);
	assert.equal(r.status, 200);
	assert.deepEqual(JSON.parse(r.body), { id: "11111111" });
	assert.equal(rows[0]?.day, "2026-10-04");
	assert.equal(rows[0]?.browser_major, 130);
	assert.equal(rows[0]?.checks, JSON.stringify([{ id: "wasm", status: "pass" }]));
});

test("invalid is 400, oversized is 413, nothing inserted", async () => {
	let n = 0;
	const ins = async (): Promise<void> => { n++; };
	const bad = await handleReport("{}", ins, now, rnd);
	assert.equal(bad.status, 400);
	assert.deepEqual(JSON.parse(bad.body), { error: "invalid report" });
	assert.equal((await handleReport(" ".repeat(5000), ins, now, rnd)).status, 413);
	assert.equal(n, 0);
});

test("insert failure is 500 without the message", async () => {
	const r = await handleReport(good, async () => { throw new Error("secret db detail"); }, now, rnd);
	assert.equal(r.status, 500);
	assert.equal(r.body, '{"error":"storage failed"}');
});
