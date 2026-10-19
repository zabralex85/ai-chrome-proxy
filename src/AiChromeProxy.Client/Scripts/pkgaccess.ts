// Read-only access to the NuGet cache folder picked in this browser (Packages tab); called through JsPackageFolder.cs (IPackageFolder).
// Nothing is ever written to the folder. The handle is remembered in its own IndexedDB database, best-effort.
// Compiled by MSBuild (Microsoft.TypeScript.MSBuild) to wwwroot/js/pkgaccess.js; the output is not committed.

/** AiChromeProxy.Client.Sync.FileMeta */
interface FileMeta {
    path: string;
    size: number;
    modified: number;
}

/** AiChromeProxy.Client.Sync.FolderGrant */
interface FolderGrant {
    name: string;
    granted: boolean;
}

const DB_NAME = 'aicp-packages';
const HANDLES = 'handles';
const KEY = 'nuget';
const MAX_DEPTH = 64;

let root: FileSystemDirectoryHandle | null = null;

function openDb(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
        if (!globalThis.indexedDB) {
            reject(new Error('IndexedDB is not available.'));
            return;
        }
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => {
            request.result.createObjectStore(HANDLES);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error('IndexedDB is blocked by another tab.'));
    });
}

async function idb<T>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const db = await openDb();
    try {
        return await new Promise<T>((resolve, reject) => {
            const tx = db.transaction(HANDLES, mode);
            const request = action(tx.objectStore(HANDLES));
            tx.oncomplete = () => resolve(request.result);
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error);
        });
    } finally {
        db.close();
    }
}

/** idb() that never throws: returns the fallback when IndexedDB fails. */
async function tryIdb<T, F>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>, fallback: F): Promise<T | F> {
    try {
        return await idb(mode, action);
    } catch {
        return fallback;
    }
}

function pickedRoot(): FileSystemDirectoryHandle {
    if (root === null) {
        throw new Error('No folder is picked.');
    }
    return root;
}

/** A relative path as its names; throws on an empty, '.' or '..' segment (or a backslash) before any handle is touched. */
function segmentsOf(path: string): string[] {
    const segments = path.split('/');
    if (segments.some(s => s === '' || s === '.' || s === '..' || s.includes('\\'))) {
        throw new Error(`Invalid path '${path}'.`);
    }
    return segments;
}

function isNotFound(e: unknown): boolean {
    return e instanceof DOMException && (e.name === 'NotFoundError' || e.name === 'TypeMismatchError');
}

/** The folder at the path under the picked folder, or null when it (or a part of the path) is missing. */
async function folderAt(segments: string[]): Promise<FileSystemDirectoryHandle | null> {
    let dir = pickedRoot();
    try {
        for (const s of segments) {
            dir = await dir.getDirectoryHandle(s);
        }
    } catch (e) {
        if (isNotFound(e)) {
            return null;
        }
        throw e;
    }
    return dir;
}

/** The file at the path under the picked folder, or null when it is missing. */
async function fileAt(path: string): Promise<File | null> {
    const segments = segmentsOf(path);
    const name = segments.pop() ?? '';
    const dir = await folderAt(segments);
    if (dir === null) {
        return null;
    }
    try {
        return await (await dir.getFileHandle(name)).getFile();
    } catch (e) {
        if (isNotFound(e)) {
            return null;
        }
        throw e;
    }
}

/** Shows the folder picker; returns the folder name, or null when the user cancelled. */
export async function pick(): Promise<string | null> {
    let handle: FileSystemDirectoryHandle;
    try {
        handle = await window.showDirectoryPicker({ id: 'aicp-pkg', mode: 'read' });
    } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') {
            return null;
        }
        throw e;
    }
    root = handle;
    await tryIdb('readwrite', s => s.put(handle, KEY), undefined);
    return handle.name;
}

/** The folder picked on an earlier visit: { name, granted }, or null. */
export async function restore(): Promise<FolderGrant | null> {
    const handle = await tryIdb<FileSystemDirectoryHandle | undefined, null>('readonly', s => s.get(KEY), null);
    if (!handle) {
        return null;
    }
    root = handle;
    return { name: handle.name, granted: (await handle.queryPermission({ mode: 'read' })) === 'granted' };
}

/** Asks for read access again; must run from a click. */
export async function requestAccess(): Promise<boolean> {
    return root !== null && (await root.requestPermission({ mode: 'read' })) === 'granted';
}

/** Forgets the remembered folder. */
export async function forget(): Promise<void> {
    root = null;
    await tryIdb('readwrite', s => s.delete(KEY), undefined);
}

/** Text of a file, or null when it is missing. */
export async function readText(path: string): Promise<string | null> {
    const file = await fileAt(path);
    return file === null ? null : await file.text();
}

/** Every file under the folder (recursive; paths relative to it, with '/'), or null when the folder is missing. */
export async function list(folder: string): Promise<FileMeta[] | null> {
    const start = await folderAt(segmentsOf(folder));
    if (start === null) {
        return null;
    }
    const found: FileMeta[] = [];
    async function walk(dir: FileSystemDirectoryHandle, prefix: string, depth: number): Promise<void> {
        if (depth > MAX_DEPTH) {
            return;
        }
        for await (const [name, handle] of dir.entries()) {
            if (handle.kind === 'directory') {
                await walk(handle as FileSystemDirectoryHandle, `${prefix}${name}/`, depth + 1);
            } else {
                const file = await (handle as FileSystemFileHandle).getFile();
                found.push({ path: `${prefix}${name}`, size: file.size, modified: file.lastModified });
            }
        }
    }
    await walk(start, '', 0);
    return found;
}

/** Bytes [offset, offset + length) of a file; fewer at its end. */
export async function readChunk(path: string, offset: number, length: number): Promise<Uint8Array> {
    const file = await fileAt(path);
    if (file === null) {
        throw new Error(`File '${path}' is missing.`);
    }
    return new Uint8Array(await file.slice(offset, offset + length).arrayBuffer());
}
