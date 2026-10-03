// File System Access API glue for the sync engine: the only things C# cannot do in the browser.
// Read-only in sub-project 3a. Called through JsFolderAccess.cs (IFolderAccess); every other decision is made in C#.

const DB_NAME = 'aicp-fsaccess';
const HANDLES = 'handles';
const HASHES = 'hashes';
const ROOT_KEY = 'root';

let root = null;        // FileSystemDirectoryHandle of the picked folder
let files = new Map();  // path -> FileSystemFileHandle, from the last scan

function openDb() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => {
            request.result.createObjectStore(HANDLES);
            request.result.createObjectStore(HASHES);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
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
        });
    } finally {
        db.close();
    }
}

function toHex(buffer) {
    return Array.from(new Uint8Array(buffer), b => b.toString(16).padStart(2, '0')).join('');
}

/** Shows the folder picker; returns the folder name, or null when the user cancelled. The handle is kept in IndexedDB. */
export async function pick() {
    try {
        root = await window.showDirectoryPicker({ id: 'aicp', mode: 'read' });
    } catch (e) {
        if (e.name === 'AbortError') {
            return null;
        }
        throw e;
    }
    files = new Map();
    await idb(HANDLES, 'readwrite', s => s.put(root, ROOT_KEY));
    return root.name;
}

/** The folder picked on an earlier visit: { name, granted }, or null. After a reload Chrome usually answers "prompt". */
export async function restore() {
    const handle = await idb(HANDLES, 'readonly', s => s.get(ROOT_KEY));
    if (!handle) {
        return null;
    }
    root = handle;
    files = new Map();
    return { name: handle.name, granted: (await handle.queryPermission({ mode: 'read' })) === 'granted' };
}

/** Asks for read access again; must run from a click. */
export async function requestAccess() {
    return root !== null && (await root.requestPermission({ mode: 'read' })) === 'granted';
}

/** Walks the folder, not descending into the given directory names. Returns { files: [{ path, size, modified }], truncated }. */
export async function scan(skipDirectories, maxEntries) {
    const skip = new Set(skipDirectories.map(d => d.toLowerCase()));
    const found = new Map();
    const list = [];
    let truncated = false;

    async function walk(dir, prefix) {
        for await (const [name, handle] of dir.entries()) {
            if (list.length >= maxEntries) {
                truncated = true;
                return;
            }
            const path = prefix + name;
            if (handle.kind === 'directory') {
                if (!skip.has(name.toLowerCase())) {
                    await walk(handle, path + '/');
                }
            } else {
                const file = await handle.getFile();
                found.set(path, handle);
                list.push({ path, size: file.size, modified: file.lastModified });
            }
        }
    }

    await walk(root, '');
    files = found;
    return { files: list, truncated };
}

/**
 * SHA-256 (lower-case hex) of each path from the last scan, or null for a file that could not be read.
 * Cached in IndexedDB per folder by size and modification time, so a rescan only hashes changed files.
 */
export async function hash(paths) {
    const key = root.name;
    const cache = (await idb(HASHES, 'readonly', s => s.get(key))) ?? {};
    const result = [];
    for (const path of paths) {
        try {
            const file = await files.get(path).getFile();
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
    await idb(HASHES, 'readwrite', s => s.put(cache, key));
    return result;
}

/** Text of a file from the last scan (used for the root .gitignore), or null when it is not there. */
export async function readText(path) {
    const handle = files.get(path);
    return handle ? await (await handle.getFile()).text() : null;
}

/** Bytes [offset, offset + length) of a file from the last scan (marshalled to byte[]; C# encodes them with SyncData.Encode). */
export async function readChunk(path, offset, length) {
    const file = await files.get(path).getFile();
    return new Uint8Array(await file.slice(offset, offset + length).arrayBuffer());
}

/** Calls callback.Changed(visible) when the tab is shown or hidden and when the window gets focus. */
export function watchVisibility(callback) {
    const notify = () => callback.invokeMethodAsync('Changed', document.visibilityState === 'visible');
    document.addEventListener('visibilitychange', notify);
    window.addEventListener('focus', notify);
}
