export const CHECK_IDS = [
	"secure-context", "browser", "wasm", "worker", "indexeddb",
	"websocket", "folder-api", "folder-read", "folder-write", "rename",
] as const;

export type CheckId = (typeof CHECK_IDS)[number];
export type CheckStatus = "pass" | "warn" | "fail" | "skipped";

export interface CheckResult {
	id: CheckId;
	status: CheckStatus;
	error?: string;
}
