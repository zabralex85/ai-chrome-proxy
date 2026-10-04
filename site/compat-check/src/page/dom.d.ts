// Browser APIs the page probes that lib.dom does not declare (Chromium-only or non-standard).

interface FileSystemHandlePermissionDescriptor {
	mode?: "read" | "readwrite";
}

interface FileSystemHandle {
	requestPermission(descriptor?: FileSystemHandlePermissionDescriptor): Promise<PermissionState>;
}

interface Window {
	showDirectoryPicker?: (options?: { mode?: "read" | "readwrite" }) => Promise<FileSystemDirectoryHandle>;
}

interface NavigatorUAData {
	readonly brands: readonly { brand: string; version: string }[];
	readonly platform: string;
}

interface Navigator {
	readonly userAgentData?: NavigatorUAData;
	readonly brave?: unknown;
}
