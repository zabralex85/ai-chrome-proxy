import type { CheckResult } from "./checks.js";

export type Verdict = "ready" | "limited" | "unsupported";

export function verdict(results: readonly CheckResult[]): Verdict {
	if (results.some((r) => r.status === "fail")) return "unsupported";
	return results.some((r) => r.status === "warn") ? "limited" : "ready";
}
