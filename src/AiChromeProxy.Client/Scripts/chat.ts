// Browser glue of the chat view that C# cannot do: Enter sends (Shift+Enter adds a line), the log follows new text unless the user scrolled up,
// and a click on a path:line link (href="#open=path:line", made by MarkdownRenderer.cs) opens the file instead of changing the address.
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
