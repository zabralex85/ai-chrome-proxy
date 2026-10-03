// File System Access API glue for the sync engine: the only things C# cannot do in the browser.
// Read-only in sub-project 3a. Called through JsFolderAccess.cs (IFolderAccess); every other decision is made in C#.
// IndexedDB (remembered folder, hash cache) is best-effort: when it fails, sync still works, only without the cache.

const DB_NAME = 'aicp-fsaccess';
const HANDLES = 'handles';
const HASHES = 'hashes';
const ROOT_KEY = 'root';
const MAX_FILE_SIZE = 20 * 1024 * 1024; // SyncLimits.MaxFileSize
const MAX_DEPTH = 64;

let root = null;        // FileSystemDirectoryHandle of the picked folder
let files = new Map();  // path -> FileSystemFileHandle, from the last scan
let chunkFile = null;   // { path, file } of the upload in progress, so its chunks come from one snapshot
let unwatch = null;     // removes the listeners added by watchVisibility

function openDb() {
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

async function idb(store, mode, action) {
    const db = await openDb();
    try {
        return await new Promise((resolve, reject) => {
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
async function tryIdb(store, mode, action, fallback) {
    try {
        return await idb(store, mode, action);
    } catch {
        return fallback;
    }
}

function setRoot(handle) {
    root = handle;
    files = new Map();
    chunkFile = null;
}

function toHex(buffer) {
    return Array.from(new Uint8Array(buffer), b => b.toString(16).padStart(2, '0')).join('');
}

/** Shows the folder picker; returns the folder name, or null when the user cancelled. The handle is kept in IndexedDB if possible. */
export async function pick() {
    let handle;
    try {
        handle = await window.showDirectoryPicker({ id: 'aicp', mode: 'read' });
    } catch (e) {
        if (e.name === 'AbortError') {
            return null;
        }
        throw e;
    }
    setRoot(handle);
    await tryIdb(HANDLES, 'readwrite', s => s.put(handle, ROOT_KEY));
    return handle.name;
}

/** The folder picked on an earlier visit: { name, granted }, or null (also when IndexedDB fails). After a reload Chrome usually answers "prompt". */
export async function restore() {
    const handle = await tryIdb(HANDLES, 'readonly', s => s.get(ROOT_KEY), null);
    if (!handle) {
        return null;
    }
    setRoot(handle);
    return { name: handle.name, granted: (await handle.queryPermission({ mode: 'read' })) === 'granted' };
}

/** Asks for read access again; must run from a click. */
export async function requestAccess() {
    return root !== null && (await root.requestPermission({ mode: 'read' })) === 'granted';
}

/**
 * Walks the folder, not descending into the given directory names. Returns { files: [{ path, size, modified }], truncated, skipped }.
 * Files and folders count toward maxEntries. skipped lists what the walk could not see, so C# never mistakes it for a deletion:
 * unreadable files by path, and folders that could not be listed (or are deeper than MAX_DEPTH) as a prefix ending in '/'.
 * Throws when the picked folder itself cannot be listed (lost access must never look like an empty folder).
 */
export async function scan(skipDirectories, maxEntries) {
    const skip = new Set(skipDirectories.map(d => d.toLowerCase()));
    const found = new Map();
    const list = [];
    let seen = 0;
    const skipped = [];
    let truncated = false;

    async function walk(dir, prefix, depth) {
        try {
            for await (const [name, handle] of dir.entries()) {
                if (++seen > maxEntries) {
                    truncated = true;
                    return;
                }
                const path = prefix + name;
                if (handle.kind === 'directory') {
                    if (skip.has(name.toLowerCase())) {
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

    await walk(root, '', 0);
    files = found;
    chunkFile = null;
    return { files: list, truncated, skipped };
}

/**
 * SHA-256 (lower-case hex) of each path from the last scan, or null for a file that could not be read or is larger than 20 MB.
 * Cached in IndexedDB per folder by size and modification time, so a rescan only hashes changed files (without IndexedDB: no cache).
 */
export async function hash(paths) {
    const key = root.name;
    const cache = (await tryIdb(HASHES, 'readonly', s => s.get(key), null)) ?? {};
    const result = [];
    for (const path of paths) {
        try {
            const file = await files.get(path).getFile();
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
    await tryIdb(HASHES, 'readwrite', s => s.put(cache, key));
    return result;
}

/** Text of a file from the last scan (used for the root .gitignore), or null when it is not there. */
export async function readText(path) {
    const handle = files.get(path);
    return handle ? await (await handle.getFile()).text() : null;
}

/**
 * Bytes [offset, offset + length) of a file from the last scan (marshalled to byte[]; C# encodes them with SyncData.Encode).
 * Offset 0 takes a snapshot of the file that later chunks of the same path read: if the file changes meanwhile, reading fails
 * instead of mixing versions.
 */
export async function readChunk(path, offset, length) {
    if (offset === 0 || chunkFile?.path !== path) {
        const handle = files.get(path);
        if (!handle) {
            throw new Error(`'${path}' is not in the last scan.`);
        }
        chunkFile = { path, file: await handle.getFile() };
    }
    return new Uint8Array(await chunkFile.file.slice(offset, offset + length).arrayBuffer());
}

/** Calls callback.Changed(visible) when the tab is shown or hidden and when the window gets focus; replaces an earlier watch. */
export function watchVisibility(callback) {
    unwatchVisibility();
    const notify = () => callback.invokeMethodAsync('Changed', document.visibilityState === 'visible');
    document.addEventListener('visibilitychange', notify);
    window.addEventListener('focus', notify);
    unwatch = () => {
        document.removeEventListener('visibilitychange', notify);
        window.removeEventListener('focus', notify);
    };
}

/** Removes the listeners added by watchVisibility, if any. */
export function unwatchVisibility() {
    unwatch?.();
    unwatch = null;
}
