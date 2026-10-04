// A read-only file viewer on Monaco: one editor per host element, highlighted by Monaco's Monarch grammars, the language picked from the path
// through Monaco's language registry. Monaco's AMD build is vendored (wwwroot/lib/monaco, MIT, see THIRD-PARTY-NOTICES.md) and pruned: no
// language-service workers, so their features are switched off and only the generic editor worker runs (editor.main points it at the vendored
// file under 'self'). The loader is a classic script, injected once on the first open, like mermaid in diagrams.ts.
// Called through JsFileViewer.cs; compiled by MSBuild to wwwroot/js/viewer.js.

interface Range {
    startLineNumber: number;
    startColumn: number;
    endLineNumber: number;
    endColumn: number;
}

interface Decoration {
    range: Range;
    options: { isWholeLine: boolean; className: string };
}

interface DecorationsCollection {
    set(decorations: Decoration[]): void;
    clear(): void;
}

interface TextModel {
    setValue(text: string): void;
    dispose(): void;
}

interface EditorOptions {
    model: TextModel;
    theme: string;
    readOnly: boolean;
    domReadOnly: boolean;
    automaticLayout: boolean;
    minimap: { enabled: boolean };
    scrollBeyondLastLine: boolean;
}

interface Editor {
    getModel(): TextModel | null;
    layout(): void;
    getScrollTop(): number;
    setScrollTop(top: number): void;
    revealLineInCenter(line: number): void;
    setPosition(position: { lineNumber: number; column: number }): void;
    createDecorationsCollection(decorations?: Decoration[]): DecorationsCollection;
    dispose(): void;
}

interface Language {
    id: string;
    extensions?: string[];
    filenames?: string[];
}

/** A language service's defaults: an empty mode configuration registers none of its features, so its worker is never started. */
interface ServiceDefaults {
    setModeConfiguration(configuration: Record<string, boolean>): void;
}

/** The part of Monaco's API (the vs/editor/editor.main module) used here. */
interface Monaco {
    editor: {
        create(host: HTMLElement, options: EditorOptions): Editor;
        createModel(text: string, language: string): TextModel;
        setTheme(theme: string): void;
    };
    languages: { getLanguages(): Language[] };
    css: { cssDefaults: ServiceDefaults; lessDefaults: ServiceDefaults; scssDefaults: ServiceDefaults };
    html: { htmlDefaults: ServiceDefaults; handlebarDefaults: ServiceDefaults; razorDefaults: ServiceDefaults };
    json: { jsonDefaults: ServiceDefaults };
    typescript: { typescriptDefaults: ServiceDefaults; javascriptDefaults: ServiceDefaults };
}

type AmdRequire = ((modules: string[], loaded: (module: Monaco) => void, failed: (error: unknown) => void) => void) & {
    config(options: { paths: Record<string, string> }): void;
};

interface Viewer {
    editor: Editor;
    revealed: DecorationsCollection;
    changed: DecorationsCollection;
    timer: number | undefined;
}

const LOADER = '/lib/monaco/vs/loader.js';
const CHANGED_FOR_MS = 2000;

/** Extensions Monaco's registry does not know: .razor is highlighted like Razor markup. */
const EXTRA_LANGUAGES: Record<string, string> = { '.razor': 'razor' };

const viewers = new Map<HTMLElement, Viewer>();
let loading: Promise<Monaco> | null = null;
let loaded: Monaco | null = null;
let chosenDark: boolean | null = null; // set by setTheme; null follows the shell's data-theme, else the system's
let watchingSystem = false;

function amdRequire(): AmdRequire | undefined {
    return (globalThis as unknown as { require?: AmdRequire }).require;
}

function load(): Promise<Monaco> {
    loading ??= new Promise<void>((resolve, reject) => {
        if (amdRequire() !== undefined) {
            resolve();
            return;
        }
        const script = document.createElement('script');
        script.src = LOADER; // the page's import map leaves lib/monaco out (ClientPage), so this plain URL is the one served
        script.onload = () => resolve();
        script.onerror = () => reject(new Error('The Monaco loader could not be loaded.'));
        document.head.appendChild(script);
    }).then(() => new Promise<Monaco>((resolve, reject) => {
        const require = amdRequire();
        if (require === undefined) {
            reject(new Error('The Monaco loader did not define require.'));
            return;
        }
        require.config({ paths: { vs: '/lib/monaco/vs' } });
        require(['vs/editor/editor.main'], monaco => {
            switchOffLanguageServices(monaco);
            loaded = monaco;
            resolve(monaco);
        }, reject);
    })).catch((e: unknown) => {
        loading = null; // a later open tries again
        throw e;
    });
    return loading;
}

/** Their workers are pruned from the vendored build; JSON keeps its tokenizer (it has no Monarch grammar), which runs without a worker. */
function switchOffLanguageServices(monaco: Monaco): void {
    for (const defaults of [monaco.css.cssDefaults, monaco.css.lessDefaults, monaco.css.scssDefaults, monaco.html.htmlDefaults,
        monaco.html.handlebarDefaults, monaco.html.razorDefaults, monaco.typescript.typescriptDefaults, monaco.typescript.javascriptDefaults]) {
        defaults.setModeConfiguration({});
    }
    monaco.json.jsonDefaults.setModeConfiguration({ tokens: true });
}

/** The language of a path by its file name or extension in Monaco's registry; plain text when none claims it. */
function languageOf(monaco: Monaco, path: string): string {
    const name = (path.split('/').pop() ?? path).toLowerCase();
    const dot = name.lastIndexOf('.');
    const extension = dot > 0 ? name.substring(dot) : null;
    const extra = extension === null ? undefined : EXTRA_LANGUAGES[extension];
    if (extra !== undefined) {
        return extra;
    }
    const language = monaco.languages.getLanguages().find(l =>
        l.filenames?.some(f => f.toLowerCase() === name) === true
        || (extension !== null && l.extensions?.some(e => e.toLowerCase() === extension) === true));
    return language?.id ?? 'plaintext';
}

/** Monaco's theme: the one set through setTheme, else the shell's data-theme, else the system's. */
function themeName(): string {
    const chosen = document.querySelector('[data-theme]')?.getAttribute('data-theme') ?? null;
    const dark = chosenDark ?? (chosen === null ? window.matchMedia('(prefers-color-scheme: dark)').matches : chosen === 'dark');
    return dark ? 'vs-dark' : 'vs';
}

function watchSystemTheme(monaco: Monaco): void {
    if (!watchingSystem) {
        watchingSystem = true;
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => monaco.editor.setTheme(themeName()));
    }
}

function lineRange(line: number): Range {
    return { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 };
}

function show(viewer: Viewer, line: number | null): void {
    if (line === null) {
        viewer.revealed.clear();
        viewer.editor.setScrollTop(0);
        return;
    }
    viewer.editor.layout(); // the tab may have just been shown again (display: none gave the editor no size)
    viewer.revealed.set([{ range: lineRange(line), options: { isWholeLine: true, className: 'viewer-revealed-line' } }]);
    viewer.editor.setPosition({ lineNumber: line, column: 1 });
    viewer.editor.revealLineInCenter(line);
}

/** Shows text in a read-only editor inside host (replacing one already there); line (1-based) is revealed in the centre and highlighted. */
export async function open(host: HTMLElement, path: string, text: string, line: number | null): Promise<void> {
    const monaco = await load();
    dispose(host);
    sweep();
    watchSystemTheme(monaco);
    const model = monaco.editor.createModel(text, languageOf(monaco, path));
    let editor: Editor;
    try {
        editor = monaco.editor.create(host, {
            model,
            theme: themeName(),
            readOnly: true,
            domReadOnly: true,
            automaticLayout: true,
            minimap: { enabled: true },
            scrollBeyondLastLine: false,
        });
    } catch (e: unknown) {
        model.dispose();
        throw e;
    }
    const viewer: Viewer = { editor, revealed: editor.createDecorationsCollection(), changed: editor.createDecorationsCollection(), timer: undefined };
    viewers.set(host, viewer);
    show(viewer, line);
}

/** Replaces the text, keeping the scroll position, and marks changedLines (1-based, in the new text) for two seconds. */
export function update(host: HTMLElement, text: string, changedLines: number[]): void {
    const viewer = viewers.get(host);
    const model = viewer?.editor.getModel();
    if (viewer === undefined || model === null || model === undefined) {
        return;
    }
    const top = viewer.editor.getScrollTop();
    model.setValue(text);
    viewer.editor.setScrollTop(top);
    viewer.changed.set(changedLines.map(l => ({ range: lineRange(l), options: { isWholeLine: true, className: 'viewer-changed-line' } })));
    window.clearTimeout(viewer.timer);
    viewer.timer = window.setTimeout(() => viewer.changed.clear(), CHANGED_FOR_MS);
}

/** Reveals and highlights line (1-based); null goes to the top and removes the highlight. */
export function reveal(host: HTMLElement, line: number | null): void {
    const viewer = viewers.get(host);
    if (viewer !== undefined) {
        show(viewer, line);
    }
}

/** Dark or light for every viewer; null follows the shell's data-theme (or the system's when none is picked) again. */
export function setTheme(dark: boolean | null): void {
    chosenDark = dark;
    loaded?.editor.setTheme(themeName()); // before the first open there is nothing to repaint: open applies it
}

/** Disposes the editor in host and its text model. */
export function dispose(host: HTMLElement | null): void {
    const viewer = host === null ? undefined : viewers.get(host);
    if (host !== null && viewer !== undefined) {
        viewers.delete(host);
        window.clearTimeout(viewer.timer);
        const model = viewer.editor.getModel();
        viewer.editor.dispose();
        model?.dispose();
    }
}

/** Disposes the editors whose host element left the page without a dispose call (a safety net: no leaked editors or models). */
function sweep(): void {
    for (const host of [...viewers.keys()]) {
        if (!host.isConnected) {
            dispose(host);
        }
    }
}
