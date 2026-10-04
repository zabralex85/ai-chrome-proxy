import { MAX_REPORT_BYTES } from "../shared/report.js";
import { handleReport } from "../shared/report-handler.js";

interface Env {
	DB: D1Database;
	ASSETS: Fetcher;
	// Optional: deployments without the "ratelimits" binding run unlimited.
	REPORT_LIMITER?: RateLimit;
}

const MAX_MESSAGES = 10;
const MAX_MS = 30_000;

function api(status: number, body: string, extra: Record<string, string> = {}): Response {
	return new Response(body, {
		status,
		headers: {
			"Content-Type": "application/json; charset=utf-8",
			"Cache-Control": "no-store",
			"X-Content-Type-Options": "nosniff",
			...extra,
		},
	});
}

const err = (status: number, message: string, extra?: Record<string, string>): Response => api(status, JSON.stringify({ error: message }), extra);

async function report(request: Request, env: Env, url: URL): Promise<Response> {
	if (request.method !== "POST") return err(405, "method not allowed", { Allow: "POST" });
	const origin = request.headers.get("Origin");
	if (origin !== null && origin !== url.origin) return err(403, "forbidden");
	// The IP is only the limiter key; it is never stored.
	if (env.REPORT_LIMITER && !(await env.REPORT_LIMITER.limit({ key: request.headers.get("CF-Connecting-IP") ?? "unknown" })).success) {
		return err(429, "too many requests", { "Retry-After": "60" });
	}
	const length = Number(request.headers.get("Content-Length") ?? "0");
	if (length > MAX_REPORT_BYTES) return err(413, "too large");
	// ponytail: reads the whole body before the size check; Content-Length is capped above, chunked bodies are bounded by the platform
	const body = await request.text();
	const r = await handleReport(
		body,
		async (row) => {
			await env.DB
				.prepare("INSERT INTO reports (id, day, browser, browser_major, os, verdict, checks) VALUES (?, ?, ?, ?, ?, ?, ?)")
				.bind(row.id, row.day, row.browser, row.browser_major, row.os, row.verdict, row.checks)
				.run();
		},
		new Date(),
		(b) => { crypto.getRandomValues(b); },
	);
	return api(r.status, r.body);
}

function echo(request: Request): Response {
	if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return err(426, "upgrade required");
	const pair = new WebSocketPair();
	const server = pair[1];
	server.accept();
	let count = 0;
	const timer = setTimeout(() => server.close(1000, "timeout"), MAX_MS);
	server.addEventListener("message", (e) => {
		if (typeof e.data === "string") server.send(e.data);
		if (++count >= MAX_MESSAGES) server.close(1000, "done");
	});
	server.addEventListener("close", () => clearTimeout(timer));
	return new Response(null, { status: 101, webSocket: pair[0] });
}

export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const url = new URL(request.url);
		if (url.pathname === "/api/report") return report(request, env, url);
		if (url.pathname === "/api/ws") return echo(request);
		if (url.pathname.startsWith("/api/")) return err(404, "not found");
		return env.ASSETS.fetch(request);
	},
} satisfies ExportedHandler<Env>;
