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
    directories: string[];
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
const MAX_VIEW_BYTES = 5 * 1024 * 1024; // FileText.MaxBytes
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
/** Whether this browser can open folders: a secure context with the File System Access API (desktop Chrome, Edge). */
export function supported(): boolean {
    return isSecureContext && 'showDirectoryPicker' in window;
}

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
 * '/a/b' only at that path; case-insensitive). Returns { files: [{ path, size, modified }], truncated, skipped, directories }.
 * Files and folders count toward maxEntries. skipped lists what the walk could not see, so C# never mistakes it for a deletion:
 * unreadable files by path, and folders that could not be listed (or are deeper than MAX_DEPTH) as a prefix ending in '/'.
 * directories lists the folder paths seen (not the skipped ones; empty folders included).
 * Throws when the picked folder itself cannot be listed (lost access must never look like an empty folder).
 */
export async function scan(skipDirectories: string[], maxEntries: number): Promise<FolderScan> {
    const skip = new Set(skipDirectories.map(d => d.toLowerCase()));
    const found = new Map<string, FileSystemFileHandle>();
    const list: FileMeta[] = [];
    let seen = 0;
    const skipped: string[] = [];
    const directories: string[] = [];
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
                    directories.push(path);
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
    // a folder that could not be listed is in skipped (as a prefix): it is not reported as seen
    return { files: list, truncated, skipped, directories: directories.filter(d => !skipped.includes(d + '/')) };
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

/** AiChromeProxy.Client.Sync.FileBytes (bytes as base64: a byte[] inside an object is read from a JSON string, not a Uint8Array) */
interface FileBytes {
    bytes: string;
    size: number;
}

/**
 * The file as it is now (not from the last scan): its first FileText.MaxBytes + 1 bytes (a huge file is never loaded whole) and its
 * size, or null when it is not there. Throws when it cannot be read or the path is invalid (before any handle is touched).
 */
export async function readFile(path: string): Promise<FileBytes | null> {
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
    const head = new Uint8Array(await file.slice(0, MAX_VIEW_BYTES + 1).arrayBuffer());
    let binary = '';
    for (let i = 0; i < head.length; i += 0x8000) {
        binary += String.fromCharCode(...head.subarray(i, i + 0x8000));
    }
    return { bytes: btoa(binary), size: file.size };
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

/** Minimal typing of FileSystemHandle.move (Chrome 110+; not in every TypeScript lib, absent from some browsers). */
interface Movable {
    move(newName: string): Promise<void>;
}

/** The handle's move function when this browser has one (feature-detected per handle). */
function moveOf(handle: FileSystemHandle): ((newName: string) => Promise<void>) | null {
    const candidate = handle as FileSystemHandle & Partial<Movable>;
    return 'move' in candidate && typeof candidate.move === 'function' ? candidate.move.bind(candidate) : null;
}

/** What the folder holds under the name (the file system decides about case): a file, a directory or nothing. */
async function entryKind(dir: FileSystemDirectoryHandle, name: string): Promise<'file' | 'directory' | null> {
    try {
        await dir.getFileHandle(name);
        return 'file';
    } catch (e) {
        if (e instanceof DOMException && e.name === 'TypeMismatchError') {
            return 'directory';
        }
        if (isNotFound(e)) {
            return null;
        }
        throw e;
    }
}

/** Creates an empty file in an existing folder; throws when the name is taken (by a file or a folder) or the path is invalid. */
export async function createFile(path: string): Promise<void> {
    const { folders, name } = splitPath(path);
    const dir = await parentOf(folders, false);
    if (await entryKind(dir, name) !== null) {
        throw new Error(`'${path}' already exists.`);
    }
    await dir.getFileHandle(name, { create: true });
}

/** Creates a folder in an existing folder; throws when the name is taken or the path is invalid. */
export async function createFolder(path: string): Promise<void> {
    const { folders, name } = splitPath(path);
    const dir = await parentOf(folders, false);
    if (await entryKind(dir, name) !== null) {
        throw new Error(`'${path}' already exists.`);
    }
    await dir.getDirectoryHandle(name, { create: true });
}

/** Whether this browser can move (rename) folders: FileSystemHandle.move exists. */
export function canRenameFolders(): boolean {
    return typeof FileSystemDirectoryHandle !== 'undefined' && 'move' in FileSystemDirectoryHandle.prototype;
}

/** Whether move() refused for this kind of folder (some local file systems) rather than for this rename: the copy path can do it instead. */
function moveUnsupported(e: unknown): boolean {
    return e instanceof DOMException && (e.name === 'NotSupportedError' || e.name === 'InvalidModificationError');
}

/**
 * Renames within the folder: a file with move (or, when there is none or it refuses as unsupported, copy: create, never overwriting, then
 * remove the old one, up to 20 MB); a folder only with move.
 */
async function moveEntry(dir: FileSystemDirectoryHandle, kind: 'file' | 'directory', from: string, to: string): Promise<void> {
    if (kind === 'directory') {
        const move = moveOf(await dir.getDirectoryHandle(from));
        if (move === null) {
            throw new Error('This browser cannot rename folders.');
        }
        await move(to);
        return;
    }
    const handle = await dir.getFileHandle(from);
    const move = moveOf(handle);
    if (move !== null) {
        try {
            await move(to);
            return;
        } catch (e) {
            if (!moveUnsupported(e)) {
                throw e;
            }
        }
    }
    const file = await handle.getFile();
    if (file.size > MAX_FILE_SIZE) {
        throw new Error(`'${from}' is larger than 20 MB; this browser cannot rename it.`);
    }
    // never open an existing target with create: a file that is there is not ours to overwrite or remove
    if (await entryKind(dir, to) !== null) {
        throw new Error(`'${to}' already exists here.`);
    }
    const target = await dir.getFileHandle(to, { create: true });
    try {
        const writable = await target.createWritable();
        try {
            await writable.write(file);
            await writable.close();
        } catch (e) {
            await writable.abort().catch(() => undefined);
            throw e;
        }
        if ((await target.getFile()).size !== file.size) {
            throw new Error(`The copy '${to}' is not as large as '${from}'.`);
        }
    } catch (e) {
        await dir.removeEntry(to).catch(() => undefined);
        throw e;
    }
    try {
        await dir.removeEntry(from);
    } catch (e) {
        throw new Error(`Copied to '${to}', but '${from}' could not be removed (${e instanceof Error ? e.message : String(e)}); both exist now.`);
    }
}

/**
 * Renames the file or folder at the path to newName (a single name) in the same folder; never overwrites, never touches the picked root
 * (an empty path is invalid). A change of case only goes through a temporary name, because the file system sees one name.
 */
export async function rename(path: string, newName: string): Promise<void> {
    const { folders, name } = splitPath(path);
    const target = splitPath(newName);
    if (target.folders.length > 0) {
        throw new Error(`Invalid name '${newName}'.`);
    }
    const dir = await parentOf(folders, false);
    const kind = await entryKind(dir, name);
    if (kind === null) {
        throw new Error(`'${path}' is not there.`);
    }
    if (name === newName) {
        return;
    }
    if (name.toLowerCase() !== newName.toLowerCase()) {
        // ponytail: check-then-move is not atomic: Chromium's move() replaces an existing target, so a name another process creates between the check and the move is lost
        // (the copy path has the same check-then-create race). No lock exists in the API; a rename racing an outside writer in the same folder is not guarded.
        if (await entryKind(dir, newName) !== null) {
            throw new Error(`'${newName}' already exists here.`);
        }
        await moveEntry(dir, kind, name, newName);
        return;
    }
    const temp = `${name}.aicp-rename`;
    if (await entryKind(dir, temp) !== null) {
        throw new Error(`'${temp}' already exists here.`);
    }
    await moveEntry(dir, kind, name, temp);
    let taken = false;
    try {
        // on a case-sensitive file system another entry can hold exactly newName: moving over it must not happen
        if (await entryKind(dir, newName) !== null) {
            taken = true;
            throw new Error(`'${newName}' already exists here.`);
        }
        await moveEntry(dir, kind, temp, newName);
    } catch (e) {
        try {
            await moveEntry(dir, kind, temp, name);
        } catch {
            const left = `'${name}' is left as '${temp}'`;
            throw new Error(taken
                ? `Both '${newName}' and '${temp}' exist; ${left}.`
                : `${e instanceof Error ? e.message : String(e)} ${left}.`);
        }
        throw e;
    }
}

/**
 * Deletes the file, or the folder with everything in it when recursive is true (a folder without it throws); no-op when it is not there.
 * The picked root is never removed (an empty path is invalid).
 */
export async function remove(path: string, recursive = false): Promise<void> {
    const { folders, name } = splitPath(path);
    let dir: FileSystemDirectoryHandle;
    let kind: 'file' | 'directory' | null;
    try {
        dir = await parentOf(folders, false);
        kind = await entryKind(dir, name);
    } catch (e) {
        if (isNotFound(e)) {
            return;
        }
        throw e;
    }
    if (kind === null) {
        return;
    }
    if (kind === 'directory') {
        if (!recursive) {
            throw new Error(`'${path}' is a folder.`);
        }
        await dir.removeEntry(name, { recursive: true });
        return;
    }
    await dir.removeEntry(name);
}

/**
 * Every file under the folder (no excludes), as it is now; the walk stops after maxEntries entries (files and folders) and below MAX_DEPTH,
 * like scan; truncated says it stopped early, so count is a lower bound. Throws when the folder is not there or cannot be listed.
 */
export async function countFiles(path: string, maxEntries: number): Promise<{ count: number; truncated: boolean }> {
    const { folders, name } = splitPath(path);
    let count = 0;
    let seen = 0;
    let truncated = false;

    async function walk(dir: FileSystemDirectoryHandle, depth: number): Promise<void> {
        for await (const [, handle] of dir.entries()) {
            if (++seen > maxEntries) {
                truncated = true;
                return;
            }
            if (handle.kind === 'directory') {
                if (depth < MAX_DEPTH) {
                    await walk(handle, depth + 1);
                } else {
                    truncated = true;
                }
            } else {
                count++;
            }
            if (seen > maxEntries) {
                return;
            }
        }
    }

    await walk(await (await parentOf(folders, false)).getDirectoryHandle(name), 0);
    return { count, truncated };
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
