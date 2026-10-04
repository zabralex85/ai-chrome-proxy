// Renders the mermaid diagrams of the chat. The markup comes from MarkdownRenderer.cs: <div class="mermaid-source" data-diagram="...">.
// mermaid.min.js is vendored (wwwroot/lib/mermaid, MIT, see THIRD-PARTY-NOTICES.md); it is a classic script that sets globalThis.mermaid,
// so it is loaded with a <script> element (as a module its top-level variables would stay private), once, on the first diagram.
// Called through JsChatView.cs; compiled by MSBuild to wwwroot/js/diagrams.js.

interface RenderResult {
    svg: string;
}

interface MermaidConfig {
    startOnLoad: boolean;
    securityLevel: 'strict';
    theme: 'dark' | 'default';
}

/** The part of mermaid's API used here. */
interface Mermaid {
    initialize(config: MermaidConfig): void;
    render(id: string, text: string): Promise<RenderResult>;
}

const RENDERED = 'data-rendered';
const LIBRARY = '/lib/mermaid/mermaid.min.js';

let loading: Promise<Mermaid> | null = null;
let counter = 0;

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

/** The shell's theme: its data-theme when the user picked one, else the system's. */
function theme(): 'dark' | 'default' {
    const chosen = document.querySelector('[data-theme]')?.getAttribute('data-theme');
    const dark = chosen === undefined || chosen === null ? window.matchMedia('(prefers-color-scheme: dark)').matches : chosen === 'dark';
    return dark ? 'dark' : 'default';
}

function showError(element: HTMLElement, source: string, error: unknown): void {
    const message = document.createElement('div');
    message.className = 'mermaid-error';
    message.setAttribute('role', 'alert');
    message.textContent = `The diagram could not be drawn: ${error instanceof Error ? error.message : String(error)}`;
    const code = document.createElement('pre');
    code.className = 'mermaid-fallback';
    code.textContent = source;
    element.replaceChildren(message, code);
}

/**
 * Draws the diagrams under the container that were not drawn yet; a diagram carries the theme it was drawn in (data-rendered), so a new
 * message does not redraw the old ones, and a theme switch redraws each of them once.
 */
export async function render(container: HTMLElement): Promise<void> {
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
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: wanted });
    } catch (e) {
        for (const element of pending) {
            showError(element, element.dataset['diagram'] ?? '', e);
        }
        return;
    }
    for (const element of pending) {
        const source = element.dataset['diagram'] ?? '';
        const id = `aicp-diagram-${++counter}`;
        try {
            element.innerHTML = (await mermaid.render(id, source)).svg;
        } catch (e) {
            showError(element, source, e);
        } finally {
            document.getElementById(`d${id}`)?.remove(); // mermaid leaves its scratch element behind on an error
        }
    }
}
