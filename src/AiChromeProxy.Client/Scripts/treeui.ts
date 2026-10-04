// Browser glue of the file tree's actions that C# cannot do: where to put the menu, selecting the stem of a name (or the name in a path), keeping the browser's own
// context menu away from the keyboard gesture, and opening the delete confirmation as a native modal dialog (focus trap, inert background, Esc).
// Called through JsTreeUi.cs; compiled by MSBuild to wwwroot/js/treeui.js.

/** AiChromeProxy.Client.Shell.TreeAnchor */
interface TreeAnchor {
    x: number;
    y: number;
    viewportWidth: number;
    viewportHeight: number;
}

/** Just under the element's left edge (or 0,0 for none), with the viewport size for keeping the menu on screen. */
export function anchor(element: HTMLElement | null): TreeAnchor {
    const rect = element?.getBoundingClientRect();
    return {
        x: rect ? rect.left + 24 : 0,
        y: rect ? rect.bottom : 0,
        viewportWidth: window.innerWidth,
        viewportHeight: window.innerHeight,
    };
}

/** Focuses the input and selects from start up to end (up to the end of the text for a negative end). */
export function selectName(input: HTMLInputElement, end: number, start = 0): void {
    input.focus();
    input.setSelectionRange(start, end < 0 ? input.value.length : end);
}

/** Stops the browser from raising its own context menu on Shift+F10 and the Menu key in the tree (the app opens its menu from the keydown). */
export function guardContextKeys(tree: HTMLElement): void {
    tree.addEventListener('keydown', (e: KeyboardEvent) => {
        if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
            e.preventDefault();
        }
    });
}

/** Opens the dialog as a modal: the rest of the page is inert, Tab stays inside, Esc raises its cancel event. */
export function showModal(dialog: HTMLDialogElement): void {
    if (!dialog.open) {
        dialog.showModal();
    }
}
