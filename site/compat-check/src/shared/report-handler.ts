import { MAX_REPORT_BYTES, newReportId, validateReport } from "./report.js";

export interface ReportRow {
	id: string;
	day: string;
	browser: string;
	browser_major: number | null;
	os: string;
	verdict: string;
	checks: string;
}

export interface HandlerResult {
	status: number;
	body: string;
}

const json = (status: number, v: unknown): HandlerResult => ({ status, body: JSON.stringify(v) });

export async function handleReport(
	body: string,
	insert: (row: ReportRow) => Promise<void>,
	now: Date,
	random: (b: Uint8Array) => void,
): Promise<HandlerResult> {
	if (new TextEncoder().encode(body).length > MAX_REPORT_BYTES) return json(413, { error: "too large" });
	const r = validateReport(body);
	if (!r) return json(400, { error: "invalid report" });
	const id = newReportId(random);
	try {
		await insert({
			id,
			day: now.toISOString().slice(0, 10),
			browser: r.browser,
			browser_major: r.browserMajor,
			os: r.os,
			verdict: r.verdict,
			checks: JSON.stringify(r.checks),
		});
	} catch {
		return json(500, { error: "storage failed" });
	}
	return json(200, { id });
}
