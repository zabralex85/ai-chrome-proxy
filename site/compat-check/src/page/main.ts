// Page wiring: runs the probes, renders rows and the verdict, copy and send.
import { CHECK_IDS, type CheckId, type CheckResult, type CheckStatus } from "../shared/checks.js";
import { MIN_CHROMIUM } from "../shared/browser.js";
import { verdict } from "../shared/verdict.js";
import { STATUS_LABELS, VERDICT_LABELS, browserLabel, buildReport, errorName, summaryText } from "../shared/summary.js";
import * as probes from "./probes.js";

const env = probes.environment();

function policyHint(): string {
	const page = env.family === "edge" ? "edge://policy" : env.family === "brave" ? "brave://policy" : "chrome://policy";
	return `It may be blocked by a policy: ask IT, or open ${page} and look for File System Access policies such as DefaultFileSystemReadGuardSetting or DefaultFileSystemWriteGuardSetting.`;
}

interface CheckText {
	title: string;
	pass: string;
	problem: () => string;
}

const TEXT: Record<CheckId, CheckText> = {
	"secure-context": {
		title: "Secure connection",
		pass: "The page runs in a secure context (HTTPS), which folder access requires.",
		problem: () => "The page is not in a secure context, so folder access is unavailable. Open it over https://.",
	},
	browser: {
		title: "Browser",
		pass: `A Chromium-based browser, version ${MIN_CHROMIUM} or newer.`,
		problem: () => (["chrome", "edge", "brave", "opera"].includes(env.family)
			? `The app needs version ${MIN_CHROMIUM} or newer (or the version could not be read). Update the browser.`
			: "The app needs a Chromium-based browser for folder access: Chrome, Edge, Brave or Opera."),
	},
	wasm: {
		title: "WebAssembly",
		pass: "The app (Blazor WebAssembly) can start.",
		problem: () => "WebAssembly is blocked, so the app cannot start. "
			+ (env.family === "edge"
				? "In Edge this is usually enhanced security mode: add an exception for the site in edge://settings (Privacy, search, and services, Enhance your security on the web)."
				: "Ask IT to allow WebAssembly for the site."),
	},
	worker: {
		title: "Background workers",
		pass: "Background workers run; the code viewer works fully.",
		problem: () => "Workers from blob: URLs are blocked, so the code viewer is limited. Usually a policy or an extension.",
	},
	indexeddb: {
		title: "Browser storage (IndexedDB)",
		pass: "The app can remember the folder and cache file hashes.",
		problem: () => "Browser storage is blocked (site data off or a private window). The app still works, but rescans every file and asks for the folder again after a reload.",
	},
	websocket: {
		title: "WebSocket connection",
		pass: "A live connection to the server works.",
		problem: () => "WebSockets are blocked, often by a proxy. The app falls back to slower transports.",
	},
	"folder-api": {
		title: "Folder access API",
		pass: "The browser can open a local folder (File System Access API).",
		problem: () => (env.family === "brave"
			? "Brave turns folder access off by default: open brave://flags/#file-system-access-api, set it to Enabled and relaunch Brave."
			: ["chrome", "edge", "opera"].includes(env.family)
				? `The folder access API is missing. ${policyHint()}`
				: "This browser has no folder access API. Use Chrome, Edge, Brave or Opera."),
	},
	"folder-read": {
		title: "Read a folder",
		pass: "The browser can list and read files in a folder you pick.",
		problem: () => `Reading the folder was refused or blocked, so the app cannot sync it. ${policyHint()}`,
	},
	"folder-write": {
		title: "Write to a folder",
		pass: "The server can write changes back to your folder.",
		problem: () => `Writing was refused or blocked. The app works one way only: the server cannot write changes back. ${policyHint()}`,
	},
	rename: {
		title: "Rename in place",
		pass: "Files and folders can be renamed in place.",
		problem: () => "This browser cannot move files in place, so renaming folders is unavailable.",
	},
};

const FOLDER_CHECKS: readonly CheckId[] = ["folder-read", "folder-write", "rename"];
const results = new Map<CheckId, CheckResult>(CHECK_IDS.map((id) => [id, { id, status: "skipped" }]));
const running = new Set<CheckId>();
let initialDone = false;
let reportSent = false;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className: string, text = ""): HTMLElementTagNameMap[K] {
	const e = document.createElement(tag);
	if (className) e.className = className;
	e.textContent = text;
	return e;
}

function byId<T extends HTMLElement>(id: string): T {
	const e = document.getElementById(id);
	if (!e) throw new Error(`#${id} missing`);
	return e as T;
}

const ordered = (): CheckResult[] => CHECK_IDS.map((id) => results.get(id) ?? { id, status: "skipped" });

function describe(r: CheckResult): string {
	const t = TEXT[r.id];
	if (running.has(r.id)) return "Checking…";
	if (r.status === "pass") return t.pass;
	if (r.status === "skipped") return FOLDER_CHECKS.includes(r.id) ? (r.id === "folder-write" ? "Not run yet: pick a test folder, then use Test writing below." : "Not run yet: use Pick a test folder below.") : "Not run.";
	return t.problem() + (r.error === undefined ? "" : ` (${r.error})`);
}

function renderRow(id: CheckId): void {
	const r = results.get(id) ?? { id, status: "skipped" };
	const status: CheckStatus | "running" = running.has(id) ? "running" : r.status;
	const li = el("li", `check ${status}`);
	li.id = `check-${id}`;
	li.append(
		el("span", `badge ${status}`, status === "running" ? "Checking…" : STATUS_LABELS[r.status]),
		el("h3", "title", id === "browser" ? `${TEXT.browser.title}: ${browserLabel(env)}` : TEXT[id].title),
		el("p", "detail", describe(r)),
	);
	byId(`check-${id}`).replaceWith(li);
}

function renderVerdict(): void {
	const title = byId("verdict-title");
	const text = byId("verdict-text");
	const list = byId<HTMLUListElement>("verdict-list");
	const box = byId("verdict");
	list.replaceChildren();
	if (!initialDone) {
		box.className = "verdict checking";
		title.textContent = "Checking…";
		text.textContent = "";
		return;
	}
	const all = ordered();
	const v = verdict(all);
	box.className = `verdict ${v}`;
	title.textContent = VERDICT_LABELS[v];
	const folderPending = all.some((r) => FOLDER_CHECKS.includes(r.id) && r.status === "skipped") && results.get("folder-api")?.status === "pass";
	if (v === "ready") {
		text.textContent = "Everything checked works in this browser." + (folderPending ? " Pick a test folder below to check folder access too." : "");
		return;
	}
	const bad = v === "unsupported" ? "fail" : "warn";
	text.textContent = v === "unsupported" ? "The app cannot run here because of:" : "The app works here, with these limitations:";
	for (const r of all.filter((x) => x.status === bad)) list.append(el("li", "", TEXT[r.id].title));
}

async function run(id: CheckId, p: () => Promise<CheckResult>): Promise<void> {
	running.add(id);
	renderRow(id);
	const r = await p();
	running.delete(id);
	results.set(id, r);
	renderRow(id);
	renderVerdict();
}

function updateSend(): void {
	byId<HTMLButtonElement>("send").disabled = !initialDone || reportSent || running.size > 0;
}

let picked: FileSystemDirectoryHandle | null = null;

async function pickFolder(): Promise<void> {
	if (!window.showDirectoryPicker) return;
	let dir: FileSystemDirectoryHandle;
	try {
		dir = await window.showDirectoryPicker({ mode: "read" });
	} catch (e) {
		if (e instanceof DOMException && e.name === "AbortError") return;
		await run("folder-read", async () => ({ id: "folder-read", status: "fail", error: errorName(e) }));
		return;
	}
	picked = null;
	const pick = byId<HTMLButtonElement>("pick");
	const write = byId<HTMLButtonElement>("write");
	pick.disabled = true;
	write.disabled = true;
	updateSend();
	try {
		await run("folder-read", () => probes.folderRead(dir));
		await run("rename", probes.rename);
	} finally {
		pick.disabled = false;
		picked = dir;
		write.disabled = false;
		updateSend();
	}
}

// A separate click: the picker consumes the user activation that requestPermission needs.
async function testWriting(): Promise<void> {
	if (picked === null) return;
	const dir = picked;
	const write = byId<HTMLButtonElement>("write");
	write.disabled = true;
	updateSend();
	try {
		await run("folder-write", () => probes.folderWrite(dir));
	} finally {
		write.disabled = false;
		updateSend();
	}
}

async function copyResult(): Promise<void> {
	const text = summaryText(env, ordered(), new Date().toISOString().slice(0, 10));
	const status = byId("copy-status");
	const area = byId<HTMLTextAreaElement>("copy-text");
	try {
		await navigator.clipboard.writeText(text);
		area.hidden = true;
		status.textContent = "Copied to the clipboard.";
	} catch {
		area.value = text;
		area.hidden = false;
		area.focus();
		area.select();
		status.textContent = "The clipboard is blocked: the text below is selected, copy it with Ctrl+C.";
	}
}

async function sendReport(): Promise<void> {
	const button = byId<HTMLButtonElement>("send");
	const status = byId("send-status");
	button.disabled = true;
	status.textContent = "Sending…";
	try {
		const res = await fetch("/api/report", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify(buildReport(env, ordered())),
		});
		const body: unknown = await res.json();
		const id = typeof body === "object" && body !== null && "id" in body && typeof body.id === "string" ? body.id : null;
		if (!res.ok || id === null) throw new Error(`HTTP ${res.status}`);
		reportSent = true;
		status.textContent = `Report id: ${id} — quote it when asking for help.`;
	} catch (e) {
		status.textContent = `The report could not be sent (${e instanceof Error ? e.message : "error"}). Try again later.`;
	} finally {
		updateSend();
	}
}

async function start(): Promise<void> {
	const list = byId<HTMLOListElement>("checks");
	for (const id of CHECK_IDS) {
		const li = el("li", "check");
		li.id = `check-${id}`;
		list.append(li);
		renderRow(id);
	}
	renderVerdict();
	byId("pick").addEventListener("click", () => void pickFolder());
	byId("write").addEventListener("click", () => void testWriting());
	byId("copy").addEventListener("click", () => void copyResult());
	byId("send").addEventListener("click", () => void sendReport());
	await Promise.all([
		run("secure-context", probes.secureContext),
		run("browser", () => probes.browser(env)),
		run("wasm", probes.wasm),
		run("worker", probes.worker),
		run("indexeddb", probes.indexedDb),
		run("websocket", probes.webSocket),
		run("folder-api", probes.folderApi),
	]);
	initialDone = true;
	renderVerdict();
	byId<HTMLButtonElement>("pick").disabled = typeof window.showDirectoryPicker !== "function";
	byId<HTMLButtonElement>("copy").disabled = false;
	updateSend();
}

void start();
