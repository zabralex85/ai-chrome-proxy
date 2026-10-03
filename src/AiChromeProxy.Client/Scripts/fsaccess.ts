// File System Access API glue for the sync engine: the only things C# cannot do in the browser.
// The picker asks for read access; writes (server changes, sub-project 3b) need write access granted from a click (requestWriteAccess).
// Called through JsFolderAccess.cs (IFolderAccess); every other decision (the hash-guard included) is made in C#.
// IndexedDB (remembered folder, hash cache) is best-effort: when it fails, sync still works, only without the cache.
// Compiled by MSBuild (Microsoft.TypeScript.MSBuild) to wwwroot/js/fsaccess.js; the output is not committed.

/** AiChromeProxy.Client.Sync.FileMeta */
interface FileMeta {
    path: string;
    size: number;
    modified: number;
}

/** AiChromeProxy.Client.Sync.FolderScan */
interface FolderScan {
    files: FileMeta[];
    truncated: boolean;
    skipped: string[];
}

/** AiChromeProxy.Client.Sync.FolderGrant */
interface FolderGrant {
    name: string;
    granted: boolean;
}

/** A DotNetObjectReference marshalled to JS (JsFolderAccess.VisibilityCallback). */
interface DotNetObject {
    invokeMethodAsync(methodName: string, ...args: unknown[]): Promise<unknown>;
}

/** Hash cache entry, per path, in the HASHES store under the folder name. */
interface HashEntry {
    size: number;
    modified: number;
    sha256: string;
}

type HashCache = Record<string, HashEntry>;

const DB_NAME = 'aicp-fsaccess';
const HANDLES = 'handles';
const HASHES = 'hashes';
const ROOT_KEY = 'root';
const MAX_FILE_SIZE = 20 * 1024 * 1024; // SyncLimits.MaxFileSize
const MAX_DEPTH = 64;

let root: FileSystemDirectoryHandle | null = null;     // the picked folder
let files = new Map<string, FileSystemFileHandle>();   // path -> handle, from the last scan
let chunkFile: { path: string; file: File } | null = null; // the upload in progress, so its chunks come from one snapshot
let unwatch: (() => void) | null = null;               // removes the listeners added by watchVisibility

function openDb(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
        if (!globalThis.indexedDB) {
            reject(new Error('IndexedDB is not available.'));
            return;
        }
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => {
            request.result.createObjectStore(HANDLES);
            request.result.createObjectStore(HASHES);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error('IndexedDB is blocked by another tab.'));
    });
}

async function idb<T>(store: string, mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const db = await openDb();
    try {
        return await new Promise<T>((resolve, reject) => {
            const tx = db.transaction(store, mode);
            const request = action(tx.objectStore(store));
            tx.oncomplete = () => resolve(request.result);
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error);
        });
    } finally {
        db.close();
    }
}

/** idb() that never throws: returns the fallback when IndexedDB fails. */
async function tryIdb<T, F>(store: string, mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>, fallback: F): Promise<T | F> {
    try {
        return await idb(store, mode, action);
    } catch {
        return fallback;
    }
}

function setRoot(handle: FileSystemDirectoryHandle): void {
    root = handle;
    files = new Map();
    chunkFile = null;
}

function pickedRoot(): FileSystemDirectoryHandle {
    if (root === null) {
        throw new Error('No folder is picked.');
    }
    return root;
}

/** A protocol path as its folder names and file name; throws on an empty, '.' or '..' segment (or a backslash) before any handle is touched. */
function splitPath(path: string): { folders: string[]; name: string } {
    const segments = path.split('/');
    if (segments.some(s => s === '' || s === '.' || s === '..' || s.includes('\\'))) {
        throw new Error(`Invalid path '${path}'.`);
    }
    const name = segments.pop() ?? '';
    return { folders: segments, name };
}

/** The folder that holds the path's file, optionally creating the missing folders. */
async function parentOf(folders: string[], create: boolean): Promise<FileSystemDirectoryHandle> {
    let dir = pickedRoot();
    for (const folder of folders) {
        dir = await dir.getDirectoryHandle(folder, { create });
    }
    return dir;
}

function isNotFound(e: unknown): boolean {
    return e instanceof DOMException && (e.name === 'NotFoundError' || e.name === 'TypeMismatchError');
}

function toHex(buffer: ArrayBuffer): string {
    return Array.from(new Uint8Array(buffer), b => b.toString(16).padStart(2, '0')).join('');
}

/** Shows the folder picker; returns the folder name, or null when the user cancelled. The handle is kept in IndexedDB if possible. */
export async function pick(): Promise<string | null> {
    let handle: FileSystemDirectoryHandle;
    try {
        handle = await window.showDirectoryPicker({ id: 'aicp', mode: 'read' });
    } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') {
            return null;
        }
        throw e;
    }
    setRoot(handle);
    await tryIdb(HANDLES, 'readwrite', s => s.put(handle, ROOT_KEY), undefined);
    return handle.name;
}

/** The folder picked on an earlier visit: { name, granted }, or null (also when IndexedDB fails). After a reload Chrome usually answers "prompt". */
export async function restore(): Promise<FolderGrant | null> {
    const handle = await tryIdb<FileSystemDirectoryHandle | undefined, null>(HANDLES, 'readonly', s => s.get(ROOT_KEY), null);
    if (!handle) {
        return null;
    }
    setRoot(handle);
    return { name: handle.name, granted: (await handle.queryPermission({ mode: 'read' })) === 'granted' };
}

/** Whether the picked folder is still readable; asks nothing (unlike requestAccess) and keeps the last scan. */
export async function hasAccess(): Promise<boolean> {
    return root !== null && (await root.queryPermission({ mode: 'read' })) === 'granted';
}

/** Asks for read access again; must run from a click. */
export async function requestAccess(): Promise<boolean> {
    return root !== null && (await root.requestPermission({ mode: 'read' })) === 'granted';
}

/**
 * Walks the folder, not descending into the given directories (IgnoreRules.SkipDirectories: a name is skipped at any depth,
 * '/a/b' only at that path; case-insensitive). Returns { files: [{ path, size, modified }], truncated, skipped }.
 * Files and folders count toward maxEntries. skipped lists what the walk could not see, so C# never mistakes it for a deletion:
 * unreadable files by path, and folders that could not be listed (or are deeper than MAX_DEPTH) as a prefix ending in '/'.
 * Throws when the picked folder itself cannot be listed (lost access must never look like an empty folder).
 */
export async function scan(skipDirectories: string[], maxEntries: number): Promise<FolderScan> {
    const skip = new Set(skipDirectories.map(d => d.toLowerCase()));
    const found = new Map<string, FileSystemFileHandle>();
    const list: FileMeta[] = [];
    let seen = 0;
    const skipped: string[] = [];
    let truncated = false;

    async function walk(dir: FileSystemDirectoryHandle, prefix: string, depth: number): Promise<void> {
        try {
            for await (const [name, handle] of dir.entries()) {
                if (++seen > maxEntries) {
                    truncated = true;
                    return;
                }
                const path = prefix + name;
                if (handle.kind === 'directory') {
                    if (skip.has(name.toLowerCase()) || skip.has('/' + path.toLowerCase())) {
                        continue;
                    }
                    if (depth >= MAX_DEPTH) {
                        skipped.push(path + '/');
                        continue;
                    }
                    await walk(handle, path + '/', depth + 1);
                    if (truncated) {
                        return;
                    }
                } else {
                    try {
                        const file = await handle.getFile();
                        found.set(path, handle);
                        list.push({ path, size: file.size, modified: file.lastModified });
                    } catch {
                        skipped.push(path);
                    }
                }
            }
        } catch (e) {
            if (depth === 0) {
                throw e;
            }
            skipped.push(prefix); // the folder could not be listed (or stopped listing); what was found so far is kept
        }
    }

    await walk(pickedRoot(), '', 0);
    files = found;
    chunkFile = null;
    return { files: list, truncated, skipped };
}

/**
 * SHA-256 (lower-case hex) of each path from the last scan, or null for a file that could not be read or is larger than 20 MB.
 * Cached in IndexedDB per folder by size and modification time, so a rescan only hashes changed files (without IndexedDB: no cache).
 */
export async function hash(paths: string[]): Promise<(string | null)[]> {
    const key = pickedRoot().name;
    const cache: HashCache = (await tryIdb<HashCache | undefined, null>(HASHES, 'readonly', s => s.get(key), null)) ?? {};
    const result: (string | null)[] = [];
    for (const path of paths) {
        try {
            const handle = files.get(path);
            if (handle === undefined) {
                result.push(null);
                continue;
            }
            const file = await handle.getFile();
            if (file.size > MAX_FILE_SIZE) {
                result.push(null);
                continue;
            }
            const hit = cache[path];
            if (hit && hit.size === file.size && hit.modified === file.lastModified) {
                result.push(hit.sha256);
                continue;
            }
            const sha256 = toHex(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()));
            cache[path] = { size: file.size, modified: file.lastModified, sha256 };
            result.push(sha256);
        } catch {
            result.push(null);
        }
    }
    for (const path of Object.keys(cache)) {
        if (!files.has(path)) {
            delete cache[path];
        }
    }
    await tryIdb(HASHES, 'readwrite', s => s.put(cache, key), undefined);
    return result;
}

/**
 * Text of a file of the picked folder, read now (the root .gitignore is read before the walk), or null when it is not there.
 * Throws when it is there but cannot be read, or when the folder itself cannot be accessed.
 */
export async function readText(path: string): Promise<string | null> {
    const names = path.split('/');
    const fileName = names.pop() ?? '';
    try {
        let dir = pickedRoot();
        for (const name of names) {
            dir = await dir.getDirectoryHandle(name);
        }
        return await (await (await dir.getFileHandle(fileName)).getFile()).text();
    } catch (e) {
        if (e instanceof DOMException && (e.name === 'NotFoundError' || e.name === 'TypeMismatchError')) {
            return null;
        }
        throw e;
    }
}

/**
 * Bytes [offset, offset + length) of a file from the last scan (marshalled to byte[]; C# encodes them with SyncData.Encode).
 * Offset 0 takes a snapshot of the file that later chunks of the same path read: if the file changes meanwhile, reading fails
 * instead of mixing versions.
 */
export async function readChunk(path: string, offset: number, length: number): Promise<Uint8Array> {
    if (offset === 0 || chunkFile?.path !== path) {
        const handle = files.get(path);
        if (!handle) {
            throw new Error(`'${path}' is not in the last scan.`);
        }
        chunkFile = { path, file: await handle.getFile() };
    }
    return new Uint8Array(await chunkFile.file.slice(offset, offset + length).arrayBuffer());
}

/** SHA-256 (lower-case hex) of the file as it is now (not from the last scan), or null when it is not there. Throws when it cannot be read. */
export async function hashNow(path: string): Promise<string | null> {
    const { folders, name } = splitPath(path);
    let file: File;
    try {
        file = await (await (await parentOf(folders, false)).getFileHandle(name)).getFile();
    } catch (e) {
        if (isNotFound(e)) {
            return null;
        }
        throw e;
    }
    return toHex(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()));
}

/** Whether the picked folder may be written; asks nothing. */
export async function hasWriteAccess(): Promise<boolean> {
    return root !== null && (await root.queryPermission({ mode: 'readwrite' })) === 'granted';
}

/** Asks for write access; must run from a click. */
export async function requestWriteAccess(): Promise<boolean> {
    return root !== null && (await root.requestPermission({ mode: 'readwrite' })) === 'granted';
}

/**
 * Replaces (or creates, with its folders) the file: createWritable writes a swap file that replaces the original only on close.
 * A file this call created is removed again when the write fails (no empty file is left behind).
 */
export async function write(path: string, bytes: Uint8Array<ArrayBuffer>): Promise<void> {
    const { folders, name } = splitPath(path);
    const dir = await parentOf(folders, true);
    let created = false;
    let handle: FileSystemFileHandle;
    try {
        handle = await dir.getFileHandle(name);
    } catch (e) {
        if (!(e instanceof DOMException && e.name === 'NotFoundError')) {
            throw e;
        }
        handle = await dir.getFileHandle(name, { create: true });
        created = true;
    }
    try {
        const writable = await handle.createWritable();
        try {
            await writable.write(bytes);
            await writable.close();
        } catch (e) {
            await writable.abort().catch(() => undefined);
            throw e;
        }
    } catch (e) {
        if (created) {
            await dir.removeEntry(name).catch(() => undefined);
        }
        throw e;
    }
}

/** Deletes the file; no-op when it is not there. Never removes a folder. */
export async function remove(path: string): Promise<void> {
    const { folders, name } = splitPath(path);
    let dir: FileSystemDirectoryHandle;
    try {
        dir = await parentOf(folders, false);
        await dir.getFileHandle(name);
    } catch (e) {
        if (e instanceof DOMException && e.name === 'NotFoundError') {
            return;
        }
        throw e;
    }
    await dir.removeEntry(name);
}

/** Calls callback.Changed(visible) when the tab is shown or hidden and when the window gets focus; replaces an earlier watch. */
export function watchVisibility(callback: DotNetObject): void {
    unwatchVisibility();
    const notify = (): Promise<unknown> => callback.invokeMethodAsync('Changed', document.visibilityState === 'visible');
    document.addEventListener('visibilitychange', notify);
    window.addEventListener('focus', notify);
    unwatch = () => {
        document.removeEventListener('visibilitychange', notify);
        window.removeEventListener('focus', notify);
    };
}

/** Removes the listeners added by watchVisibility, if any. */
export function unwatchVisibility(): void {
    unwatch?.();
    unwatch = null;
}
