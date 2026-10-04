// Renders the mermaid diagrams of the chat. The markup comes from MarkdownRenderer.cs: <div class="mermaid-source" data-diagram="...">.
// mermaid.min.js is vendored (wwwroot/lib/mermaid, MIT, see THIRD-PARTY-NOTICES.md); it is a classic script that sets globalThis.mermaid,
// so it is loaded with a <script> element (as a module its top-level variables would stay private), once, on the first diagram.
// Each drawn diagram gets a toolbar: Open (a full-window <dialog> viewer with zoom and pan, CSS transforms on the SVG), Save… (into the project:
// .NET asks for the path, then calls exportSvg/exportPng for the bytes) and Download… (SVG/PNG through a blob link). The PNG is drawn from an SVG
// with SVG text labels (htmlLabels off: foreignObject labels would taint the canvas) loaded as a data: image, which the CSP's img-src allows.
// Called through JsChatView.cs; compiled by MSBuild to wwwroot/js/diagrams.js.

interface RenderResult {
    svg: string;
}

interface MermaidConfig {
    startOnLoad: boolean;
    securityLevel: 'strict';
    theme: 'dark' | 'default';
    htmlLabels?: boolean;
    flowchart?: { htmlLabels: boolean };
    class?: { htmlLabels: boolean };
}

/** The part of mermaid's API used here. */
interface Mermaid {
    initialize(config: MermaidConfig): void;
    render(id: string, text: string): Promise<RenderResult>;
}

/** A DotNetObjectReference marshalled to JS (Shell/DiagramCallback.cs). */
interface DotNetObject {
    invokeMethodAsync<T>(methodName: string, ...args: unknown[]): Promise<T>;
}

/** AiChromeProxy.Client.Chat.DiagramFileKind */
type Kind = 'Source' | 'Svg' | 'Png';

interface Picture {
    text: string;
    width: number;
    height: number;
}

interface MenuItem {
    label: string;
    save: boolean;
    run: () => Promise<void>;
}

interface Point {
    x: number;
    y: number;
}

interface Viewer {
    dialog: HTMLDialogElement;
    title: HTMLElement;
    level: HTMLElement;
    actions: HTMLElement;
    viewport: HTMLElement;
    stage: HTMLElement;
}

const RENDERED = 'data-rendered';
const LIBRARY = '/lib/mermaid/mermaid.min.js';
const SVG_NS = 'http://www.w3.org/2000/svg';
const NO_FOLDER = 'Pick a folder first';
const MIN_ZOOM = 0.1;
const MAX_ZOOM = 8;
const ZOOM_STEP = 1.25;
const PAN_STEP = 40;
const FIT_MARGIN = 24;
const PNG_SCALE = 2;
const PNG_MAX_SIDE = 8192;
const PROBLEM_MS = 8000;

const ICONS = {
    open: 'M2.5 6V2.5H6M10 2.5h3.5V6M13.5 10v3.5H10M6 13.5H2.5V10',
    save: 'M2.5 2.5h8.5l2.5 2.5v8.5h-11zM5 2.5v3.5h5.5V2.5M5 13.5V9.5h6v4',
    download: 'M8 2.5v8M4.5 7.5L8 11l3.5-3.5M2.5 13.5h11',
};

let loading: Promise<Mermaid> | null = null;
let counter = 0;
let watching: { container: HTMLElement; callback: DotNetObject | null } | null = null; // what to redraw when the system theme changes
let queue: Promise<unknown> = Promise.resolve();
let folderOpen = false;
let openMenu: { wrap: HTMLElement; button: HTMLButtonElement; list: HTMLElement } | null = null;
let viewer: Viewer | null = null;
let opener: HTMLElement | null = null;
let problem: { element: HTMLElement; timer: number } | null = null;
const view = { x: 0, y: 0, scale: 1, width: 0, height: 0, fitted: true };

function loaded(): Mermaid | undefined {
    return (globalThis as unknown as { mermaid?: Mermaid }).mermaid;
}

/** The library's URL: the fingerprinted one through the page's import map when it has the file, else the plain path. */
function libraryUrl(): string {
    try {
        return import.meta.resolve(LIBRARY);
    } catch {
        return LIBRARY;
    }
}

function load(): Promise<Mermaid> {
    loading ??= new Promise<Mermaid>((resolve, reject) => {
        const existing = loaded();
        if (existing !== undefined) {
            resolve(existing);
            return;
        }
        const script = document.createElement('script');
        script.src = libraryUrl();
        script.onload = () => {
            const mermaid = loaded();
            if (mermaid === undefined) {
                reject(new Error('mermaid.min.js did not define mermaid.'));
            } else {
                resolve(mermaid);
            }
        };
        script.onerror = () => reject(new Error('mermaid.min.js could not be loaded.'));
        document.head.appendChild(script);
    }).catch((e: unknown) => {
        loading = null; // a later message tries again
        throw e;
    });
    return loading;
}

/** Runs the mermaid work one call at a time, so an export's configuration never leaks into a redraw of the chat (and back). */
function serial<T>(work: () => Promise<T>): Promise<T> {
    const run = queue.then(work, work);
    queue = run.catch(() => undefined);
    return run;
}

/** The shell's theme: its data-theme when the user picked one, else the system's. */
function theme(): 'dark' | 'default' {
    const chosen = document.querySelector('[data-theme]')?.getAttribute('data-theme');
    const dark = chosen === undefined || chosen === null ? window.matchMedia('(prefers-color-scheme: dark)').matches : chosen === 'dark';
    return dark ? 'dark' : 'default';
}

function config(mode: 'dark' | 'default'): MermaidConfig {
    return { startOnLoad: false, securityLevel: 'strict', theme: mode };
}

/** Draws one diagram under a fresh id and removes mermaid's scratch element (left behind on an error). */
async function draw(mermaid: Mermaid, source: string): Promise<string> {
    const id = `aicp-diagram-${++counter}`;
    try {
        return (await mermaid.render(id, source)).svg;
    } finally {
        document.getElementById(`d${id}`)?.remove();
    }
}

function report(e: unknown): void {
    console.error('Diagram action failed:', e);
}

function clearProblem(): void {
    if (problem !== null) {
        clearTimeout(problem.timer);
        problem.element.remove();
        problem = null;
    }
}

/** A failed menu action: logged, and said next to its menu (in the toolbar, or before the viewer's actions) until the next action or for 8 s. */
function fail(wrap: HTMLElement, e: unknown): void {
    report(e);
    clearProblem();
    const element = document.createElement('span');
    element.className = 'diagram-problem';
    element.setAttribute('role', 'alert');
    element.setAttribute('data-testid', 'diagram-problem');
    element.textContent = `Could not create the picture: ${e instanceof Error ? e.message : String(e)}`;
    const bar = wrap.parentElement;
    if (bar?.classList.contains('diagram-viewer-actions') === true) {
        bar.before(element);
    } else {
        bar?.prepend(element);
    }
    problem = { element, timer: window.setTimeout(clearProblem, PROBLEM_MS) };
}

function icon(path: string): SVGSVGElement {
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', 'icon');
    svg.setAttribute('width', '14');
    svg.setAttribute('height', '14');
    svg.setAttribute('viewBox', '0 0 16 16');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');
    const shape = document.createElementNS(SVG_NS, 'path');
    shape.setAttribute('d', path);
    svg.appendChild(shape);
    return svg;
}

function button(label: string, iconPath: string | null = null): HTMLButtonElement {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'diagram-button';
    if (iconPath !== null) {
        b.appendChild(icon(iconPath));
    }
    b.append(label);
    b.addEventListener('click', clearProblem); // any next action hides a shown failure
    return b;
}

// Menus: a button (aria-haspopup) and a role="menu" list under it; arrows/Home/End move, Esc or a pick closes and returns to the button,
// Tab or a click elsewhere closes. One menu is open at a time.

function menuItems(list: HTMLElement): HTMLButtonElement[] {
    return Array.from(list.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'));
}

function closeMenu(refocus: boolean): void {
    const menu = openMenu;
    if (menu === null) {
        return;
    }
    openMenu = null;
    menu.list.hidden = true;
    menu.button.setAttribute('aria-expanded', 'false');
    if (refocus) {
        menu.button.focus();
    }
}

function showMenu(wrap: HTMLElement, button: HTMLButtonElement, list: HTMLElement, last: boolean): void {
    closeMenu(false);
    openMenu = { wrap, button, list };
    list.hidden = false;
    button.setAttribute('aria-expanded', 'true');
    const items = menuItems(list);
    (last ? items[items.length - 1] : items[0])?.focus();
}

document.addEventListener('pointerdown', (e: PointerEvent) => {
    if (openMenu !== null && !(e.target instanceof Node && openMenu.wrap.contains(e.target))) {
        closeMenu(false);
    }
}, true);

function applySaveState(item: HTMLElement): void {
    if (folderOpen) {
        item.removeAttribute('aria-disabled');
        item.removeAttribute('title');
    } else {
        item.setAttribute('aria-disabled', 'true');
        item.title = NO_FOLDER;
    }
}

function menu(label: string, iconPath: string, items: MenuItem[]): HTMLElement {
    const wrap = document.createElement('div');
    wrap.className = 'diagram-menu';
    const trigger = button(label, iconPath);
    const list = document.createElement('div');
    list.className = 'tree-menu diagram-menu-list';
    list.id = `aicp-diagram-menu-${++counter}`;
    list.setAttribute('role', 'menu');
    list.setAttribute('aria-label', label.replace('…', ''));
    list.hidden = true;
    trigger.setAttribute('aria-haspopup', 'menu');
    trigger.setAttribute('aria-expanded', 'false');
    trigger.setAttribute('aria-controls', list.id);

    for (const item of items) {
        const entry = document.createElement('button');
        entry.type = 'button';
        entry.className = 'tree-menu-item';
        entry.setAttribute('role', 'menuitem');
        entry.tabIndex = -1;
        entry.textContent = item.label;
        if (item.save) {
            entry.setAttribute('data-diagram-save', '');
            applySaveState(entry);
        }
        entry.addEventListener('click', () => {
            if (entry.getAttribute('aria-disabled') === 'true') {
                return;
            }
            clearProblem();
            closeMenu(true);
            item.run().catch((e: unknown) => fail(wrap, e));
        });
        list.appendChild(entry);
    }

    trigger.addEventListener('click', () => {
        if (openMenu?.wrap === wrap) {
            closeMenu(true);
        } else {
            showMenu(wrap, trigger, list, false);
        }
    });
    trigger.addEventListener('keydown', (e: KeyboardEvent) => {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            showMenu(wrap, trigger, list, e.key === 'ArrowUp');
        }
    });
    list.addEventListener('keydown', (e: KeyboardEvent) => {
        const entries = menuItems(list);
        const at = entries.indexOf(document.activeElement as HTMLButtonElement);
        let next: number | null = null;
        switch (e.key) {
            case 'ArrowDown':
                next = (at + 1) % entries.length;
                break;
            case 'ArrowUp':
                next = (at - 1 + entries.length) % entries.length;
                break;
            case 'Home':
                next = 0;
                break;
            case 'End':
                next = entries.length - 1;
                break;
            case 'Escape':
                e.preventDefault();
                e.stopPropagation(); // inside the viewer, Esc closes the menu, not the dialog
                closeMenu(true);
                return;
            case 'Tab':
                closeMenu(false);
                return;
            default:
                return;
        }
        e.preventDefault();
        entries[next]?.focus();
    });

    wrap.append(trigger, list);
    return wrap;
}

function saveMenu(source: string, callback: DotNetObject, drawn: boolean): HTMLElement {
    const save = (kind: Kind) => () => callback.invokeMethodAsync<void>('Save', kind, source);
    const items: MenuItem[] = [{ label: 'Source (.mmd)', save: true, run: save('Source') }];
    if (drawn) {
        items.push({ label: 'SVG', save: true, run: save('Svg') }, { label: 'PNG', save: true, run: save('Png') });
    }
    return menu('Save…', ICONS.save, items);
}

function downloadMenu(source: string, callback: DotNetObject | null): HTMLElement {
    return menu('Download…', ICONS.download, [
        { label: 'SVG', save: false, run: () => download(source, 'Svg', callback) },
        { label: 'PNG', save: false, run: () => download(source, 'Png', callback) },
    ]);
}

/** The toolbar of a diagram: Open and Download… for a drawn one, Save… when .NET listens (only the source for one that failed). */
function toolbar(source: string, callback: DotNetObject | null, svg: SVGSVGElement | null): HTMLElement {
    const bar = document.createElement('div');
    bar.className = 'diagram-toolbar';
    bar.setAttribute('role', 'toolbar');
    bar.setAttribute('aria-label', 'Diagram');
    bar.setAttribute('data-testid', 'diagram-toolbar');
    if (svg !== null) {
        const open = button('Open', ICONS.open);
        open.addEventListener('click', () => openViewer(source, svg, open, callback));
        bar.appendChild(open);
    }
    if (callback !== null) {
        bar.appendChild(saveMenu(source, callback, svg !== null));
    }
    if (svg !== null) {
        bar.appendChild(downloadMenu(source, callback));
    }
    return bar;
}

function showError(element: HTMLElement, source: string, error: unknown, callback: DotNetObject | null): void {
    const message = document.createElement('div');
    message.className = 'mermaid-error';
    message.setAttribute('role', 'alert');
    message.textContent = `The diagram could not be drawn: ${error instanceof Error ? error.message : String(error)}`;
    const code = document.createElement('pre');
    code.className = 'mermaid-fallback';
    code.textContent = source;
    element.replaceChildren(...(callback === null ? [] : [toolbar(source, callback, null)]), message, code);
}

function showDiagram(element: HTMLElement, source: string, svg: string, callback: DotNetObject | null): void {
    const canvas = document.createElement('div');
    canvas.className = 'diagram-canvas';
    canvas.innerHTML = svg; // mermaid's output with securityLevel 'strict' (sanitized by its DOMPurify)
    element.replaceChildren(toolbar(source, callback, canvas.querySelector('svg')), canvas);
}

/** Without a theme picked in the shell the system's applies: when it changes, the diagrams are drawn again in the new one. */
function watchSystemTheme(container: HTMLElement, callback: DotNetObject | null): void {
    const first = watching === null;
    watching = { container, callback };
    if (first) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            if (watching?.container.isConnected === true) {
                void render(watching.container, watching.callback);
            }
        });
    }
}

/**
 * Draws the diagrams under the container that were not drawn yet; a diagram carries the theme it was drawn in (data-rendered), so a new
 * message does not redraw the old ones, and a theme switch redraws each of them once (with a fresh toolbar). The callback receives the Save
 * clicks (Save(kind, source)) and names the downloads (Name(source)); without one the Save menu is left out and downloads are named "diagram".
 */
export function render(container: HTMLElement, callback: DotNetObject | null = null): Promise<void> {
    watchSystemTheme(container, callback);
    return serial(() => renderNow(container, callback));
}

async function renderNow(container: HTMLElement, callback: DotNetObject | null): Promise<void> {
    const wanted = theme();
    const pending = Array.from(container.querySelectorAll<HTMLElement>('.mermaid-source')).filter(e => e.getAttribute(RENDERED) !== wanted);
    if (pending.length === 0) {
        return;
    }
    for (const element of pending) {
        element.setAttribute(RENDERED, wanted);
    }
    let mermaid: Mermaid;
    try {
        mermaid = await load();
        mermaid.initialize(config(wanted));
    } catch (e) {
        for (const element of pending) {
            showError(element, element.dataset['diagram'] ?? '', e, callback);
        }
        return;
    }
    for (const element of pending) {
        const source = element.dataset['diagram'] ?? '';
        try {
            showDiagram(element, source, await draw(mermaid, source), callback);
        } catch (e) {
            showError(element, source, e, callback);
        }
    }
}

/** Save items are enabled only while a folder is open; applies to the items on screen and to those drawn later. */
export function setFolderOpen(open: boolean): void {
    folderOpen = open;
    document.querySelectorAll<HTMLElement>('[data-diagram-save]').forEach(applySaveState);
}

// Export: a fresh drawing in the current theme (the one on screen may be zoomed), made standalone: its viewBox size as width/height and a
// background rect in the colour diagrams are shown on.

function viewBox(svg: SVGSVGElement): [number, number, number, number] {
    const values = (svg.getAttribute('viewBox') ?? '').trim().split(/[\s,]+/).map(Number);
    const [x = 0, y = 0, width = 0, height = 0] = values;
    if (values.length !== 4 || !values.every(Number.isFinite) || width <= 0 || height <= 0) {
        throw new Error('The diagram has no size.');
    }
    return [x, y, width, height];
}

/** The background diagrams are shown on in the chat, else the theme's default. */
function background(mode: 'dark' | 'default'): string {
    const shown = document.querySelector('.mermaid-source');
    const color = shown === null ? '' : getComputedStyle(shown).backgroundColor;
    return color !== '' && color !== 'transparent' && color !== 'rgba(0, 0, 0, 0)' ? color : mode === 'dark' ? '#1e1e1e' : '#ffffff';
}

async function picture(source: string, htmlLabels: boolean): Promise<Picture> {
    const mode = theme();
    const markup = await serial(async () => {
        const mermaid = await load();
        mermaid.initialize(htmlLabels ? config(mode) : { ...config(mode), htmlLabels: false, flowchart: { htmlLabels: false }, class: { htmlLabels: false } });
        try {
            return await draw(mermaid, source);
        } finally {
            if (!htmlLabels) {
                mermaid.initialize(config(mode));
            }
        }
    });
    const template = document.createElement('template');
    template.innerHTML = markup;
    const svg = template.content.querySelector('svg');
    if (svg === null) {
        throw new Error('mermaid returned no SVG.');
    }
    const [x, y, width, height] = viewBox(svg);
    svg.setAttribute('width', String(width));
    svg.setAttribute('height', String(height));
    svg.style.removeProperty('max-width');
    const back = document.createElementNS(SVG_NS, 'rect');
    back.setAttribute('x', String(x));
    back.setAttribute('y', String(y));
    back.setAttribute('width', String(width));
    back.setAttribute('height', String(height));
    back.setAttribute('fill', background(mode));
    svg.insertBefore(back, svg.firstChild);
    return { text: `<?xml version="1.0" encoding="UTF-8"?>\n${new XMLSerializer().serializeToString(svg)}`, width, height };
}

/** The diagram as a standalone SVG file (UTF-8). */
export async function exportSvg(source: string): Promise<Uint8Array<ArrayBuffer>> {
    return new TextEncoder().encode((await picture(source, true)).text);
}

/** The diagram as a PNG at 2× (the longer side capped at 8192 px), labels as SVG text. */
export async function exportPng(source: string): Promise<Uint8Array<ArrayBuffer>> {
    const { text, width, height } = await picture(source, false);
    const image = new Image();
    image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(text)}`;
    await image.decode();
    const scale = Math.min(PNG_SCALE, PNG_MAX_SIDE / Math.max(width, height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(width * scale));
    canvas.height = Math.max(1, Math.round(height * scale));
    const context = canvas.getContext('2d');
    if (context === null) {
        throw new Error('No canvas to draw the PNG on.');
    }
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob>((resolve, reject) =>
        canvas.toBlob(b => (b === null ? reject(new Error('The PNG could not be made.')) : resolve(b)), 'image/png'));
    return new Uint8Array(await blob.arrayBuffer());
}

async function download(source: string, kind: 'Svg' | 'Png', callback: DotNetObject | null): Promise<void> {
    const name = callback === null ? 'diagram' : await callback.invokeMethodAsync<string>('Name', source);
    const bytes = kind === 'Svg' ? await exportSvg(source) : await exportPng(source);
    const url = URL.createObjectURL(new Blob([bytes], { type: kind === 'Svg' ? 'image/svg+xml' : 'image/png' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name + (kind === 'Svg' ? '.svg' : '.png');
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

// Viewer: one modal <dialog> over the whole window; the SVG sits on a stage moved with translate() scale() (vector-sharp at any zoom).
// It is fitted on open and on resize until the user zooms or pans.

function apply(v: Viewer): void {
    v.stage.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.scale})`;
    v.level.textContent = `${Math.round(view.scale * 100)}%`;
}

function fit(v: Viewer): void {
    const width = v.viewport.clientWidth;
    const height = v.viewport.clientHeight;
    // A small diagram is shown at 100 %, never blown up to the window.
    view.scale = Math.max(MIN_ZOOM, Math.min(1, (width - 2 * FIT_MARGIN) / view.width, (height - 2 * FIT_MARGIN) / view.height));
    view.x = (width - view.width * view.scale) / 2;
    view.y = (height - view.height * view.scale) / 2;
    view.fitted = true;
    apply(v);
}

/** Zooms by the factor keeping the viewport point (px, py) in place; the centre when no point is given. */
function zoom(v: Viewer, factor: number, px = v.viewport.clientWidth / 2, py = v.viewport.clientHeight / 2): void {
    const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, view.scale * factor));
    view.x = px - (px - view.x) * next / view.scale;
    view.y = py - (py - view.y) * next / view.scale;
    view.scale = next;
    view.fitted = false;
    apply(v);
}

function pan(v: Viewer, dx: number, dy: number): void {
    view.x += dx;
    view.y += dy;
    view.fitted = false;
    apply(v);
}

/**
 * Two fingers moved from (a0, b0) to (a1, b1) (viewport points): the scale follows the distance between them (within 10 %–800 %) and the
 * point of the diagram under their old midpoint moves to the new one (zoom and pan in one).
 */
function pinch(v: Viewer, a0: Point, b0: Point, a1: Point, b1: Point): void {
    const before = Math.hypot(b0.x - a0.x, b0.y - a0.y);
    const next = before > 0 ? Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, view.scale * Math.hypot(b1.x - a1.x, b1.y - a1.y) / before)) : view.scale;
    view.x = (a1.x + b1.x) / 2 - ((a0.x + b0.x) / 2 - view.x) * next / view.scale;
    view.y = (a1.y + b1.y) / 2 - ((a0.y + b0.y) / 2 - view.y) * next / view.scale;
    view.scale = next;
    view.fitted = false;
    apply(v);
}

function createViewer(): Viewer {
    const dialog = document.createElement('dialog');
    dialog.className = 'diagram-viewer';
    dialog.setAttribute('aria-labelledby', 'aicp-diagram-viewer-title');
    dialog.setAttribute('data-testid', 'diagram-viewer');

    const header = document.createElement('div');
    header.className = 'diagram-viewer-header';
    const title = document.createElement('h2');
    title.id = 'aicp-diagram-viewer-title';
    title.className = 'diagram-viewer-title';
    const level = document.createElement('span');
    level.className = 'diagram-viewer-level';
    level.title = 'Zoom level';
    level.setAttribute('data-testid', 'diagram-zoom');
    const zoomOut = button('Zoom out');
    const zoomIn = button('Zoom in');
    const fitButton = button('Fit');
    const actual = button('100%');
    const actions = document.createElement('div');
    actions.className = 'diagram-viewer-actions';
    const close = button('Close');
    header.append(title, level, zoomOut, zoomIn, fitButton, actual, actions, close);

    const viewport = document.createElement('div');
    viewport.className = 'diagram-viewport';
    viewport.tabIndex = 0;
    viewport.setAttribute('aria-label', 'Diagram: drag or use the arrow keys to move, + and - to zoom, 0 to fit, 1 for 100%');
    const stage = document.createElement('div');
    stage.className = 'diagram-stage';
    viewport.appendChild(stage);
    dialog.append(header, viewport);
    document.body.appendChild(dialog);

    const v: Viewer = { dialog, title, level, actions, viewport, stage };
    zoomOut.addEventListener('click', () => zoom(v, 1 / ZOOM_STEP));
    zoomIn.addEventListener('click', () => zoom(v, ZOOM_STEP));
    fitButton.addEventListener('click', () => fit(v));
    actual.addEventListener('click', () => zoom(v, 1 / view.scale));
    close.addEventListener('click', () => dialog.close());

    dialog.addEventListener('keydown', (e: KeyboardEvent) => {
        const target = e.target instanceof Element ? e.target : null;
        if (e.ctrlKey || e.metaKey || e.altKey || target?.closest('[role="menu"], [aria-haspopup]') != null) {
            return;
        }
        const keys: Record<string, () => void> = {
            '+': () => zoom(v, ZOOM_STEP),
            '=': () => zoom(v, ZOOM_STEP),
            '-': () => zoom(v, 1 / ZOOM_STEP),
            '0': () => fit(v),
            '1': () => zoom(v, 1 / view.scale),
            ArrowLeft: () => pan(v, PAN_STEP, 0),
            ArrowRight: () => pan(v, -PAN_STEP, 0),
            ArrowUp: () => pan(v, 0, PAN_STEP),
            ArrowDown: () => pan(v, 0, -PAN_STEP),
        };
        const action = keys[e.key];
        if (action !== undefined) {
            e.preventDefault();
            action();
        }
    });
    viewport.addEventListener('wheel', (e: WheelEvent) => {
        e.preventDefault();
        const rect = viewport.getBoundingClientRect();
        const delta = e.deltaMode === WheelEvent.DOM_DELTA_LINE ? e.deltaY * 40 : e.deltaY;
        zoom(v, Math.exp(-delta * 0.002), e.clientX - rect.left, e.clientY - rect.top);
    }, { passive: false });

    // One pointer drags the diagram; two (fingers) pinch: zoom by their distance around their midpoint, pan with it.
    const pointers = new Map<number, Point>();
    const at = (e: PointerEvent): Point => {
        const rect = viewport.getBoundingClientRect();
        return { x: e.clientX - rect.left, y: e.clientY - rect.top };
    };
    viewport.addEventListener('pointerdown', (e: PointerEvent) => {
        if (e.button !== 0) {
            return;
        }
        e.preventDefault(); // no text selection starts in the diagram
        window.getSelection()?.removeAllRanges();
        viewport.focus();
        try {
            viewport.setPointerCapture(e.pointerId);
        } catch {
            // the pointer is gone already
        }
        pointers.set(e.pointerId, at(e));
        viewport.classList.add('dragging');
    });
    viewport.addEventListener('pointermove', (e: PointerEvent) => {
        const last = pointers.get(e.pointerId);
        if (last === undefined) {
            return;
        }
        const now = at(e);
        if (pointers.size === 1) {
            pan(v, now.x - last.x, now.y - last.y);
        } else if (pointers.size === 2) {
            const other = Array.from(pointers).find(([id]) => id !== e.pointerId)![1];
            pinch(v, last, other, now, other);
        }
        pointers.set(e.pointerId, now);
    });
    const end = (e: PointerEvent): void => {
        pointers.delete(e.pointerId);
        if (pointers.size === 0) {
            viewport.classList.remove('dragging');
        }
    };
    viewport.addEventListener('pointerup', end);
    viewport.addEventListener('pointercancel', end);

    window.addEventListener('resize', () => {
        if (dialog.open && view.fitted) {
            fit(v);
        }
    });
    dialog.addEventListener('close', () => {
        closeMenu(false);
        clearProblem();
        pointers.clear();
        viewport.classList.remove('dragging');
        v.stage.replaceChildren();
        if (opener?.isConnected === true) {
            opener.focus();
        }
        opener = null;
    });
    return v;
}

function openViewer(source: string, svg: SVGSVGElement, from: HTMLElement, callback: DotNetObject | null): void {
    const v = (viewer ??= createViewer());
    const clone = svg.cloneNode(true) as SVGSVGElement;
    let width: number;
    let height: number;
    try {
        [, , width, height] = viewBox(clone);
    } catch (e) {
        report(e);
        return;
    }
    clone.setAttribute('width', String(width));
    clone.setAttribute('height', String(height));
    clone.style.maxWidth = 'none';
    v.stage.replaceChildren(clone);
    v.title.textContent = 'diagram';
    if (callback !== null) {
        callback.invokeMethodAsync<string>('Name', source).then(name => {
            v.title.textContent = name;
        }, report);
    }
    v.actions.replaceChildren(...(callback === null ? [] : [saveMenu(source, callback, true)]), downloadMenu(source, callback));
    v.dialog.style.colorScheme = theme() === 'dark' ? 'dark' : 'light'; // outside the shell, which carries the picked theme
    opener = from;
    view.width = width;
    view.height = height;
    v.dialog.showModal();
    fit(v);
    v.viewport.focus();
}
