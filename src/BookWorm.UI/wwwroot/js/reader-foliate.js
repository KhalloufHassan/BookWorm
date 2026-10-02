// EPUB, MOBI, AZW3, FB2 and CBZ, rendered by foliate-js. It's imported by bare name; the host's
// import map says where from (see ReaderLibraries.cs).
import { makeBook } from 'foliate-js/view.js'
import { Overlayer } from 'foliate-js/overlayer.js'
import { contextOf, findQuote, cleanSelectionText } from './anchoring.js'
import { protectBook } from './sanitize.js'
import { HIGHLIGHT_COLORS, THEMES, clientRectOf, debounce } from './reader-common.js'

const FONTS = {
    serif: '"Iowan Old Style", "Palatino Linotype", Palatino, "Book Antiqua", Georgia, serif',
    sans: 'system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans", "Helvetica Neue", Arial, sans-serif',
}

const MARGINS = { narrow: '3%', normal: '6%', wide: '12%' }

const flattenToc = (items, depth = 0, list = []) => {
    for (const item of items ?? []) {
        list.push({ label: (item.label ?? '').trim() || 'Untitled', href: item.href ?? '', depth })
        flattenToc(item.subitems, depth + 1, list)
    }
    return list
}

const stylesFor = settings => {
    const theme = THEMES[settings.theme] ?? THEMES.light
    const recolor = settings.theme !== 'light'
    return `
        html { color-scheme: ${theme.scheme}; font-size: ${settings.fontSize}% !important; }
        html, body { background: ${theme.background} !important; color: ${theme.text} !important; }
        ${recolor ? `body *:not(a):not(a *) { color: inherit !important; background-color: transparent !important; border-color: ${theme.muted} !important; }
        a, a * { color: ${theme.link} !important; }` : ''}
        ${settings.fontFamily !== 'publisher' ? `body, body * { font-family: ${FONTS[settings.fontFamily] ?? FONTS.serif} !important; }` : ''}
        p, li, blockquote, dd, div { line-height: ${settings.lineHeight} !important; }
        p { text-align: ${settings.justify ? 'justify' : 'start'} !important; hyphens: ${settings.justify ? 'auto' : 'manual'}; }
        img, svg, video { max-width: 100%; }
        pre { white-space: pre-wrap !important; }
        ::selection { background: ${theme.selection}; }
    `
}

export class FoliateReader {
    #view
    #book
    #dotnet
    #settings
    #highlights = new Map()   // location → highlight
    #lastLocation = {}
    #tocItems = []
    #searchRun = 0
    #selectionTimer
    #skipTap = false
    #sectionFractions = []

    constructor(host, dotnet) {
        this.host = host
        this.#dotnet = dotnet
    }

    async open({ url, fileName, location, progress, settings, highlights }) {
        const response = await fetch(url, { credentials: 'same-origin' })
        if (!response.ok) throw new Error(response.status === 404 ? 'The book file is missing on the server.' : `The book couldn't be downloaded (${response.status}).`)
        const file = new File([await response.blob()], fileName)

        this.#book = await makeBook(file)
        protectBook(this.#book)

        const view = document.createElement('foliate-view')
        view.className = 'bw-foliate'
        this.host.append(view)
        this.#view = view
        await view.open(this.#book)

        view.addEventListener('load', e => this.#onLoad(e.detail))
        view.addEventListener('relocate', e => this.#onRelocate(e.detail))
        view.addEventListener('create-overlay', e => this.#drawSection(e.detail.index))
        view.addEventListener('draw-annotation', e => {
            const highlight = this.#highlights.get(e.detail.annotation.value)
            e.detail.draw(Overlayer.highlight, { color: HIGHLIGHT_COLORS[highlight?.color] ?? HIGHLIGHT_COLORS.Yellow })
        })
        view.addEventListener('show-annotation', e => {
            const highlight = this.#highlights.get(e.detail.value)
            if (!highlight) return
            this.#skipTap = true
            this.#dotnet.invokeMethodAsync('OnHighlightClicked', highlight.id, clientRectOf(e.detail.range))
        })
        view.addEventListener('external-link', e => {
            e.preventDefault()
            const href = e.detail.href_ ?? e.detail.a?.href
            if (/^(https?:|mailto:)/i.test(href ?? '')) window.open(href, '_blank', 'noopener,noreferrer')
        })

        this.#sectionFractions = view.getSectionFractions()
        this.#tocItems = flattenToc(this.#book.toc)
        this.applySettings(settings)
        this.setHighlights(highlights)

        // A saved location can be stale (e.g. the file was replaced); fall back to the saved progress.
        const fallback = progress > 0 ? { fraction: progress } : null
        let start = location && view.resolveNavigation(location) ? location : fallback
        try {
            await view.init({ lastLocation: start, showTextStart: !start })
        } catch {
            await view.init({ lastLocation: fallback, showTextStart: !fallback })
        }

        return {
            toc: this.#tocItems,
            isFixedLayout: view.isFixedLayout,
            hasPageList: (this.#book.pageList?.length ?? 0) > 0,
            direction: this.#book.dir ?? 'ltr',
        }
    }

    applySettings(settings) {
        this.#settings = settings
        const renderer = this.#view.renderer
        const theme = THEMES[settings.theme] ?? THEMES.light
        this.host.style.setProperty('--bw-reader-page', theme.background)
        this.#view.style.setProperty('--overlayer-highlight-opacity', theme.scheme === 'dark' ? '.4' : '.45')
        this.#view.style.setProperty('--overlayer-highlight-blend-mode', theme.scheme === 'dark' ? 'screen' : 'multiply')
        if (this.#view.isFixedLayout) {
            renderer.setAttribute('zoom', settings.zoom ?? 'fit-page')
            return
        }
        renderer.setAttribute('flow', settings.flow === 'scrolled' ? 'scrolled' : 'paginated')
        renderer.setAttribute('gap', MARGINS[settings.margin] ?? MARGINS.normal)
        renderer.setAttribute('margin', '32px')
        renderer.setAttribute('max-inline-size', `${settings.maxWidth ?? 720}px`)
        renderer.setAttribute('max-column-count', settings.columns === 'one' ? '1' : '2')
        renderer.setStyles?.(stylesFor(settings))
    }

    next() { return this.#view.goRight() }
    prev() { return this.#view.goLeft() }
    goTo(target) { return this.#view.goTo(target) }
    goToFraction(fraction) { return this.#view.goToFraction(fraction) }

    // Highlights

    setHighlights(list) {
        for (const location of this.#highlights.keys()) this.#view.deleteAnnotation({ value: location })
        this.#highlights = new Map()
        for (const highlight of list ?? []) this.addHighlight(highlight)
    }

    addHighlight(highlight) {
        if (highlight.state !== 'Anchored' || !highlight.location) return
        this.#highlights.set(highlight.location, highlight)
        this.#view.addAnnotation({ value: highlight.location }).catch(() => {})
    }

    removeHighlight(id) {
        for (const [location, highlight] of this.#highlights) {
            if (highlight.id === id) {
                this.#highlights.delete(location)
                this.#view.deleteAnnotation({ value: location }).catch(() => {})
            }
        }
    }

    #drawSection(index) {
        for (const location of this.#highlights.keys()) {
            try {
                if (this.#view.resolveCFI(location).index === index) this.#view.addAnnotation({ value: location })
            } catch { /* A location from another file version; the reader re-anchors those. */ }
        }
    }

    /** Looks for each passage in the current file. Returns where each was found, or that it wasn't. */
    async reanchor(items) {
        const pending = new Map(items.map(item => [item.id, item]))
        const results = []
        for (const [index, section] of this.#book.sections.entries()) {
            if (!pending.size) break
            if (!section.createDocument) continue
            let doc
            try { doc = await section.createDocument() } catch { continue }
            for (const item of [...pending.values()]) {
                const range = doc.body ? findQuote(doc.body, item) : null
                if (!range) continue
                pending.delete(item.id)
                const { tocItem, pageItem } = this.#view.getProgressOf(index, range)
                results.push({
                    id: item.id,
                    found: true,
                    location: this.#view.getCFI(index, range),
                    chapter: tocItem?.label?.trim() ?? null,
                    pageLabel: pageItem?.label ?? null,
                    position: Math.min(1, this.#sectionFractions[index] ?? 0),
                })
            }
        }
        for (const item of pending.values()) results.push({ id: item.id, found: false })
        return results
    }

    // Search

    async search(query) {
        const run = ++this.#searchRun
        this.#view.clearSearch()
        if (!query?.trim()) return
        let count = 0
        for await (const result of this.#view.search({ query: query.trim() })) {
            if (run !== this.#searchRun) return
            if (result === 'done') break
            if (result.progress != null) {
                await this.#dotnet.invokeMethodAsync('OnSearchProgress', result.progress)
            }
            if (result.subitems) {
                const batch = result.subitems.slice(0, 500 - count).map(item => ({
                    label: result.label?.trim() || '',
                    location: item.cfi,
                    before: item.excerpt.pre,
                    match: item.excerpt.match,
                    after: item.excerpt.post,
                }))
                count += batch.length
                if (batch.length) await this.#dotnet.invokeMethodAsync('OnSearchResults', batch)
                if (count >= 500) break
            }
        }
        if (run === this.#searchRun) await this.#dotnet.invokeMethodAsync('OnSearchDone', count)
    }

    clearSearch() {
        this.#searchRun++
        this.#view.clearSearch()
    }

    clearSelection() { this.#view.deselect() }

    destroy() {
        this.#searchRun++
        this.#view?.close()
        this.#view?.remove()
    }

    // Events

    #onLoad({ doc, index }) {
        const checkSoon = () => {
            clearTimeout(this.#selectionTimer)
            this.#selectionTimer = setTimeout(() => this.#checkSelection(doc, index), 250)
        }
        doc.addEventListener('pointerup', checkSoon)
        doc.addEventListener('keyup', checkSoon)
        doc.addEventListener('selectionchange', debounce(() => this.#checkSelection(doc, index), 600))
        doc.addEventListener('keydown', e => window.dispatchEvent(new CustomEvent('bw-reader-key', { detail: e })))
        doc.addEventListener('click', e => {
            if (e.target.closest?.('a[href]')) return
            setTimeout(() => this.#onTap(doc, e), 0)
        })
        for (const type of ['pointermove', 'wheel', 'keydown', 'touchstart']) {
            doc.addEventListener(type, () => window.dispatchEvent(new Event('bw-reader-activity')), { passive: true })
        }
    }

    #onTap(doc, event) {
        if (this.#skipTap) {
            this.#skipTap = false
            return
        }
        if (!doc.getSelection()?.isCollapsed) return
        const frame = doc.defaultView.frameElement?.getBoundingClientRect()
        const x = (frame?.left ?? 0) + event.clientX
        const width = window.innerWidth
        const zone = x < width * 0.25 ? 'left' : x > width * 0.75 ? 'right' : 'center'
        if (zone === 'left') this.#view.goLeft()
        else if (zone === 'right') this.#view.goRight()
        else this.#dotnet.invokeMethodAsync('OnTap')
    }

    #checkSelection(doc, index) {
        const selection = doc.getSelection()
        if (!selection || selection.isCollapsed || !selection.rangeCount) {
            this.#dotnet.invokeMethodAsync('OnSelection', null)
            return
        }
        const range = selection.getRangeAt(0)
        const text = cleanSelectionText(range.toString())
        if (!text) return
        const { prefix, suffix } = contextOf(doc.body, range)
        const { tocItem, pageItem } = this.#view.getProgressOf(index, range)
        this.#dotnet.invokeMethodAsync('OnSelection', {
            text,
            location: this.#view.getCFI(index, range),
            prefix,
            suffix,
            chapter: tocItem?.label?.trim() ?? this.#lastLocation.chapter ?? null,
            pageLabel: pageItem?.label ?? this.#lastLocation.pageLabel ?? null,
            position: Math.min(1, Math.max(0, this.#lastLocation.progress ?? 0)),
            rect: clientRectOf(range),
        })
    }

    #onRelocate(detail) {
        const fixed = this.#view.isFixedLayout
        const sections = this.#book.sections.length
        const index = detail.section?.current ?? this.#view.resolveNavigation(detail.cfi)?.index ?? 0
        // foliate reports progress at the end of the visible page, so the last page is 1.
        const progress = Math.min(1, Math.max(0, detail.fraction ?? (sections > 1 ? index / (sections - 1) : 1)))
        const location = detail.location
        this.#lastLocation = {
            location: detail.cfi,
            progress,
            chapter: detail.tocItem?.label?.trim() ?? null,
            pageLabel: detail.pageItem?.label ?? null,
            pageNumber: fixed ? index + 1 : (location ? location.current + 1 : null),
            pageCount: fixed ? sections : (location?.total ?? null),
            atEnd: index >= sections - 1 && progress >= 0.999,
        }
        this.#dotnet.invokeMethodAsync('OnRelocated', this.#lastLocation)
    }
}
