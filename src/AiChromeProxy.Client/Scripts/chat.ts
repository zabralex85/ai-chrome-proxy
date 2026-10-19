// Browser glue of the chat view that C# cannot do: Enter sends (Shift+Enter adds a line), the log follows new text unless the user scrolled up,
// and a click on a path:line or path#Symbol link (href="#open=path:line" or "#open=path#Symbol", made by MarkdownRenderer.cs) opens the file instead of changing the address.
// Files pasted, dropped or picked into the chat box are kept here under a handle and read in chunks by ChatAttachments.cs.
// Called through JsChatView.cs; compiled by MSBuild to wwwroot/js/chat.js.

/** A DotNetObjectReference marshalled to JS (JsChatView.SendCallback). */
interface DotNetObject {
    invokeMethodAsync(methodName: string, ...args: unknown[]): Promise<unknown>;
}

const FOLLOW_DISTANCE = 48; // px from the bottom that still counts as "at the end"

const following = new WeakMap<HTMLElement, boolean>();

/** Sends the textarea's text to callback.Submit(text) on Enter (not Shift+Enter, not while an input method composes). */
export function sendOnEnter(textarea: HTMLTextAreaElement, callback: DotNetObject): void {
    textarea.addEventListener('keydown', (e: KeyboardEvent) => {
        if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            void callback.invokeMethodAsync('Submit', textarea.value);
        }
    });
}

/** Calls callback.Open(href) for a click on a path:line link inside the log. */
export function openLinks(log: HTMLElement, callback: DotNetObject): void {
    log.addEventListener('click', (e: MouseEvent) => {
        const link = e.target instanceof Element ? e.target.closest('a[href^="#open="]') : null;
        if (link !== null) {
            e.preventDefault();
            void callback.invokeMethodAsync('Open', link.getAttribute('href'));
        }
    });
}

/** Keeps track of whether the user is at the end of the log (call once per element). */
export function follow(log: HTMLElement): void {
    if (following.has(log)) {
        return;
    }
    following.set(log, true);
    log.addEventListener('scroll', () => {
        following.set(log, log.scrollHeight - log.scrollTop - log.clientHeight <= FOLLOW_DISTANCE);
    });
}

/** After a render: scrolls to the end when the user was at the end. */
export function scrollToEnd(log: HTMLElement): void {
    follow(log);
    if (following.get(log) === true) {
        log.scrollTop = log.scrollHeight;
    }
}

/** Puts the text cursor in the textarea. */
export function focus(textarea: HTMLElement): void {
    textarea.focus();
}

/** A file reported to C# (JsChatView.AttachCallback.Add -> AttachmentFile). */
interface AttachedFile {
    handle: string;
    name: string;
    size: number;
    type: string;
    preview: string | null;
}

/** Image types Chrome renders in an img (heic, tiff and the like get the file icon). */
const PREVIEWABLE = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp', 'image/svg+xml', 'image/avif']);

const attached = new Map<string, { file: File; preview: string | null }>();
let strayGuarded = false;
let lastHandle = 0;

/** Pasted screenshots arrive as "image.png": name them by the local time instead, so that several stay apart. */
function nameOf(file: File, pasted: boolean): string {
    if (!pasted || !file.type.startsWith('image/') || (file.name !== '' && file.name !== 'image.png')) {
        return file.name === '' ? 'file' : file.name;
    }
    const d = new Date();
    const two = (n: number): string => String(n).padStart(2, '0');
    const ext = file.type.slice('image/'.length).replace('jpeg', 'jpg').replace(/[^a-z0-9]/g, '') || 'png';
    return `pasted-${d.getFullYear()}${two(d.getMonth() + 1)}${two(d.getDate())}-${two(d.getHours())}${two(d.getMinutes())}${two(d.getSeconds())}.${ext}`;
}

/** Keeps the files under new handles and reports them to callback.Add(files); false when there were none. */
function report(list: FileList | null | undefined, pasted: boolean, callback: DotNetObject): boolean {
    if (list === null || list === undefined || list.length === 0) {
        return false;
    }
    const added: AttachedFile[] = [];
    for (const file of Array.from(list)) {
        const handle = `f${++lastHandle}`;
        const preview = PREVIEWABLE.has(file.type) ? URL.createObjectURL(file) : null;
        attached.set(handle, { file, preview });
        added.push({ handle, name: nameOf(file, pasted), size: file.size, type: file.type, preview });
    }
    callback.invokeMethodAsync('Add', added).catch(() => {
        for (const f of added) {
            forgetAttachment(f.handle);
        }
    });
    return true;
}

/** Files pasted into, dropped on or picked in the chat box go to callback.Add(files); pasting text and dragging text stay as they are. */
export function acceptFiles(zone: HTMLElement, picker: HTMLInputElement, callback: DotNetObject): void {
    const hasFiles = (e: DragEvent): boolean => e.dataTransfer?.types.includes('Files') === true;
    if (!strayGuarded) {
        strayGuarded = true;
        // A file dropped just outside the box would otherwise open in the tab and leave the app.
        const guard = (e: DragEvent): void => {
            if (hasFiles(e) && !(e.target instanceof Node && zone.contains(e.target))) {
                e.preventDefault();
            }
        };
        window.addEventListener('dragover', guard);
        window.addEventListener('drop', guard);
    }
    zone.addEventListener('paste', (e: ClipboardEvent) => {
        // Excel, Word and OneNote put text and a bitmap on the clipboard: the text wins.
        if (e.clipboardData?.getData('text/plain').trim()) {
            return;
        }
        if (report(e.clipboardData?.files, true, callback)) {
            e.preventDefault();
        }
    });
    zone.addEventListener('dragover', (e: DragEvent) => {
        if (hasFiles(e)) {
            e.preventDefault();
            zone.classList.add('dragging');
        }
    });
    zone.addEventListener('dragleave', (e: DragEvent) => {
        if (!(e.relatedTarget instanceof Node && zone.contains(e.relatedTarget))) {
            zone.classList.remove('dragging');
        }
    });
    zone.addEventListener('drop', (e: DragEvent) => {
        zone.classList.remove('dragging');
        if (hasFiles(e)) {
            e.preventDefault();
            report(e.dataTransfer?.files, false, callback);
        }
    });
    picker.addEventListener('change', () => {
        report(picker.files, false, callback);
        picker.value = '';
    });
}

/** Up to length bytes of a kept file from offset (none when it was forgotten). */
export async function readAttachment(handle: string, offset: number, length: number): Promise<Uint8Array> {
    const entry = attached.get(handle);
    if (entry === undefined) {
        return new Uint8Array(0);
    }
    return new Uint8Array(await entry.file.slice(offset, offset + length).arrayBuffer());
}

/** Drops a kept file and its preview. */
export function forgetAttachment(handle: string): void {
    const entry = attached.get(handle);
    if (entry?.preview) {
        URL.revokeObjectURL(entry.preview);
    }
    attached.delete(handle);
}
