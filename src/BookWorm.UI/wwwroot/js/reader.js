// The book reader: picks the engine for the file's format and handles what both have in common
// (keyboard, activity for reading time, saving the position when the page is closed).
import { FoliateReader } from './reader-foliate.js'

const ACTIVITY_THROTTLE = 20_000

class ReaderController {
    #engine
    #dotnet
    #info
    #beacon = null
    #lastActivity = 0
    #listeners = []

    constructor(engine, dotnet, info) {
        this.#engine = engine
        this.#dotnet = dotnet
        this.#info = info

        this.#listen(window, 'keydown', e => this.#onKey(e))
        this.#listen(window, 'bw-reader-key', e => this.#onKey(e.detail))
        this.#listen(window, 'bw-reader-activity', () => this.#onActivity())
        this.#listen(window, 'pointermove', () => this.#onActivity())
        this.#listen(window, 'pagehide', () => this.#sendBeacon())
        this.#listen(document, 'visibilitychange', () => {
            if (document.visibilityState === 'hidden') this.#sendBeacon()
            this.#dotnet.invokeMethodAsync('OnVisibilityChanged', document.visibilityState === 'visible')
        })
    }

    info() { return this.#info }

    next() { return this.#engine.next() }
    prev() { return this.#engine.prev() }
    goTo(target) { return this.#engine.goTo(target) }
    goToFraction(fraction) { return this.#engine.goToFraction(fraction) }
    applySettings(settings) { this.#engine.applySettings(settings) }
    zoom(direction) { return this.#engine.zoom?.(direction) ?? null }

    setHighlights(list) { this.#engine.setHighlights(list) }
    addHighlight(highlight) { this.#engine.addHighlight(highlight) }
    removeHighlight(id) { this.#engine.removeHighlight(id) }
    reanchor(items) { return this.#engine.reanchor(items) }

    search(query) { return this.#engine.search(query) }
    clearSearch() { this.#engine.clearSearch() }
    clearSelection() { this.#engine.clearSelection() }

    copyText(text) { return navigator.clipboard?.writeText(text) }

    /** The latest progress to save if the page is closed before the app gets to it. */
    setBeacon(url, body) { this.#beacon = { url, body } }

    destroy() {
        for (const [target, type, handler] of this.#listeners) target.removeEventListener(type, handler)
        this.#engine.destroy()
    }

    #listen(target, type, handler) {
        target.addEventListener(type, handler)
        this.#listeners.push([target, type, handler])
    }

    #onKey(event) {
        const target = event.target
        if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return
        if (target?.closest?.('input, textarea, select, [contenteditable="true"], .mud-dialog, .mud-drawer')) return
        const rtl = this.#info.direction === 'rtl'
        switch (event.key) {
            case 'ArrowRight': rtl ? this.#engine.prev() : this.#engine.next(); break
            case 'ArrowLeft': rtl ? this.#engine.next() : this.#engine.prev(); break
            case 'PageDown': this.#engine.next(); break
            case 'PageUp': this.#engine.prev(); break
            case ' ': event.shiftKey ? this.#engine.prev() : this.#engine.next(); break
            case 'Escape': this.#dotnet.invokeMethodAsync('OnEscape'); return
            default: return
        }
        event.preventDefault?.()
        this.#onActivity()
    }

    #onActivity() {
        const now = Date.now()
        if (now - this.#lastActivity < ACTIVITY_THROTTLE) return
        this.#lastActivity = now
        this.#dotnet.invokeMethodAsync('OnActivity')
    }

    #sendBeacon() {
        if (!this.#beacon) return
        const { url, body } = this.#beacon
        fetch(url, {
            method: 'PUT',
            body,
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            keepalive: true,
        }).catch(() => {})
    }
}

export async function open(host, options, dotnet) {
    host.replaceChildren()
    const engine = options.format === 'Pdf'
        ? new (await import('./reader-pdf.js')).PdfReader(host, dotnet)
        : new FoliateReader(host, dotnet)
    const info = await engine.open(options)
    return new ReaderController(engine, dotnet, info)
}
