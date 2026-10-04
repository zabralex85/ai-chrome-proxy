// One probe per check. Every probe resolves to a CheckResult and never throws.
import { browserStatus, detectBrowser, detectOs } from "../shared/browser.js";
import type { CheckId, CheckResult, CheckStatus } from "../shared/checks.js";
import { errorName, type Environment } from "../shared/summary.js";

const TIMEOUT_MS = 5000;
const IDB_NAME = "aicp-compat-probe";

async function probe(id: CheckId, onError: CheckStatus, run: () => Promise<CheckStatus>): Promise<CheckResult> {
	try {
		return { id, status: await run() };
	} catch (e) {
		return { id, status: onError, error: errorName(e) };
	}
}

function withTimeout<T>(executor: (resolve: (v: T) => void, reject: (e: unknown) => void) => void): Promise<T> {
	return new Promise<T>((resolve, reject) => {
		const timer = setTimeout(() => reject(new DOMException("Timed out", "TimeoutError")), TIMEOUT_MS);
		executor(
			(v) => { clearTimeout(timer); resolve(v); },
			(e) => { clearTimeout(timer); reject(e); },
		);
	});
}

function randomHex(bytes: number): string {
	return Array.from(crypto.getRandomValues(new Uint8Array(bytes)), (b) => b.toString(16).padStart(2, "0")).join("");
}

export function environment(): Environment {
	const ua = navigator.userAgentData;
	const b = detectBrowser(ua?.brands ?? null, navigator.userAgent, "brave" in navigator);
	return { family: b.family, major: b.major, os: detectOs(ua?.platform ?? null, navigator.userAgent) };
}

export const secureContext = (): Promise<CheckResult> =>
	probe("secure-context", "fail", async () => (globalThis.isSecureContext ? "pass" : "fail"));

export const browser = (env: Environment): Promise<CheckResult> =>
	probe("browser", "fail", async () => browserStatus(env.family, env.major));

export const wasm = (): Promise<CheckResult> =>
	probe("wasm", "fail", async () => {
		await WebAssembly.instantiate(new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]));
		return "pass";
	});

export const worker = (): Promise<CheckResult> =>
	probe("worker", "warn", async () => {
		const url = URL.createObjectURL(new Blob(["onmessage = (e) => postMessage(e.data);"], { type: "text/javascript" }));
		try {
			const w = new Worker(url);
			try {
				await withTimeout<void>((resolve, reject) => {
					w.onmessage = () => resolve();
					w.onerror = (e) => { e.preventDefault(); reject(new DOMException("Worker failed", "NetworkError")); };
					w.postMessage("ping");
				});
			} finally {
				w.terminate();
			}
		} finally {
			URL.revokeObjectURL(url);
		}
		return "pass";
	});

export const indexedDb = (): Promise<CheckResult> =>
	probe("indexeddb", "warn", async () => {
		await withTimeout<void>((resolve, reject) => {
			const r = indexedDB.open(IDB_NAME);
			r.onsuccess = () => { r.result.close(); resolve(); };
			r.onerror = () => reject(r.error);
		});
		await withTimeout<void>((resolve, reject) => {
			const r = indexedDB.deleteDatabase(IDB_NAME);
			r.onsuccess = () => resolve();
			r.onerror = () => reject(r.error);
		});
		return "pass";
	});

export const webSocket = (): Promise<CheckResult> =>
	probe("websocket", "warn", async () => {
		const ws = new WebSocket((location.protocol === "https:" ? "wss:" : "ws:") + "//" + location.host + "/api/ws");
		try {
			await withTimeout<void>((resolve, reject) => {
				ws.onopen = () => ws.send("ping");
				ws.onmessage = (e) => (e.data === "ping" ? resolve() : reject(new DOMException("Unexpected reply", "DataError")));
				ws.onerror = () => reject(new DOMException("WebSocket failed", "NetworkError"));
				ws.onclose = () => reject(new DOMException("WebSocket closed", "NetworkError"));
			});
		} finally {
			ws.close();
		}
		return "pass";
	});

export const folderApi = (): Promise<CheckResult> =>
	probe("folder-api", "fail", async () => (typeof window.showDirectoryPicker === "function" ? "pass" : "fail"));

export const folderRead = (dir: FileSystemDirectoryHandle): Promise<CheckResult> =>
	probe("folder-read", "fail", async () => {
		let count = 0;
		let file: FileSystemFileHandle | null = null;
		for await (const h of dir.values()) {
			if (file === null && h.kind === "file") file = h;
			if (++count >= 100) break;
		}
		if (file !== null) await (await file.getFile()).slice(0, 64).arrayBuffer();
		return "pass";
	});

export const folderWrite = (dir: FileSystemDirectoryHandle): Promise<CheckResult> =>
	probe("folder-write", "warn", async () => {
		if ((await dir.requestPermission({ mode: "readwrite" })) !== "granted") throw new DOMException("Write refused", "NotAllowedError");
		const name = `.aicp-compat-${randomHex(4)}.tmp`;
		const data = crypto.getRandomValues(new Uint8Array(16));
		const handle = await dir.getFileHandle(name, { create: true });
		try {
			const w = await handle.createWritable();
			await w.write(data);
			await w.close();
			const back = new Uint8Array(await (await handle.getFile()).arrayBuffer());
			if (back.length !== data.length || back.some((b, i) => b !== data[i])) throw new DOMException("Read back differs", "DataError");
		} finally {
			await dir.removeEntry(name);
		}
		return "pass";
	});

export const rename = (): Promise<CheckResult> =>
	probe("rename", "warn", async () =>
		(typeof FileSystemFileHandle !== "undefined" && "move" in FileSystemFileHandle.prototype ? "pass" : "warn"));
