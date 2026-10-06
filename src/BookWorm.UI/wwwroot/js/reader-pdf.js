// PDFs, rendered by PDF.js's viewer components: continuous scrolling, zoom, text selection and search.
// PDF.js is imported by bare name; the host's import map says where from (see ReaderLibraries.cs).
// From a CDN its worker is cross-origin, which PDF.js handles by starting it from a same-origin blob.
import * as pdfjsLib from 'pdfjs-dist/build/pdf.min.mjs'
import { bestMatch, cleanSelectionText, contextOf, findQuote } from './anchoring.js'
import { HIGHLIGHT_COLORS, THEMES, debounce } from './reader-common.js'

// The viewer components expect the library as a global.
globalThis.pdfjsLib = pdfjsLib
const viewerLib = await import('pdfjs-dist/web/pdf_viewer.mjs')

const BASE = import.meta.resolve('pdfjs-dist/')
pdfjsLib.GlobalWorkerOptions.workerSrc = `${BASE}build/pdf.worker.min.mjs`

if (!document.querySelector('link[data-bw-pdfjs]')) {
    const link = document.createElement('link')
    link.rel = 'stylesheet'
    link.href = `${BASE}web/pdf_viewer.css`
    link.dataset.bwPdfjs = ''
    document.head.append(link)
}

export const PDF_OPTIONS = {
    cMapUrl: `${BASE}cmaps/`,
    cMapPacked: true,
    standardFontDataUrl: `${BASE}standard_fonts/`,
    wasmUrl: `${BASE}wasm/`,
    iccUrl: `${BASE}iccs/`,
    isEvalSupported: false,
    enableXfa: false,
}

export { pdfjsLib }

const PAGE = /^page:(\d+)/

export class PdfReader {
    #dotnet
    #container
    #viewer
    #eventBus
    #pdf
    #labels = null
    #outline = []
    #highlights = []
    #drawn = new Map()   // page → [{ id, rects }]
    #pageTexts = new Map()
    #searchRun = 0
    #lastQuery = ''

    constructor(host, dotnet) {
        this.host = host
        this.#dotnet = dotnet
    }

    get pageCount() { return this.#pdf?.numPages ?? 0 }

    async open({ url, location, progress, settings, highlights }) {
        const container = document.createElement('div')
        container.className = 'bw-pdf'
        const viewerElement = document.createElement('div')
        viewerElement.className = 'pdfViewer'
        container.append(viewerElement)
        this.host.append(container)
        this.#container = container

        const eventBus = new viewerLib.EventBus()
        const linkService = new viewerLib.PDFLinkService({
            eventBus,
            externalLinkTarget: viewerLib.LinkTarget.BLANK,
            externalLinkRel: 'noopener noreferrer nofollow',
            // Links inside a PDF can ask for their own zoom ("fit the page", or a tiny one); keep the reader's.
            ignoreDestinationZoom: true,
        })
        const findController = new viewerLib.PDFFindController({ eventBus, linkService })
        const viewer = new viewerLib.PDFViewer({
            container,
            eventBus,
            linkService,
            findController,
            textLayerMode: 1,
            annotationMode: pdfjsLib.AnnotationMode.ENABLE,
        })
        linkService.setViewer(viewer)
        this.#viewer = viewer
        this.#eventBus = eventBus

        try {
            // PDF.js resolves relative URLs against the page address, not <base href>.
            this.#pdf = await pdfjsLib.getDocument({ url: new URL(url, document.baseURI).href, withCredentials: true, ...PDF_OPTIONS }).promise
        } catch (e) {
            throw new Error(e?.status === 404 ? 'The book file is missing on the server.' : `The PDF couldn't be opened: ${e?.message ?? e}`)
        }

        const pagesReady = new Promise(resolve => eventBus.on('pagesinit', resolve, { once: true }))
        viewer.setDocument(this.#pdf)
        linkService.setDocument(this.#pdf)
        await pagesReady

        this.#labels = await this.#pdf.getPageLabels().catch(() => null)
        this.#outline = await this.#readOutline()
        this.applySettings(settings)
        this.#highlights = highlights ?? []

        eventBus.on('pagechanging', () => this.#reportLocation())
        eventBus.on('textlayerrendered', e => this.#drawPage(e.pageNumber))
        container.addEventListener('scroll', debounce(() => this.#reportLocation(), 150), { passive: true })
        container.addEventListener('pointerup', () => setTimeout(() => this.#checkSelection(), 50))
        container.addEventListener('keyup', () => this.#checkSelection())
        document.addEventListener('selectionchange', this.#onSelectionChange)
        container.addEventListener('click', e => this.#onClick(e))
        for (const type of ['pointermove', 'wheel', 'touchstart', 'scroll']) {
            container.addEventListener(type, () => window.dispatchEvent(new Event('bw-reader-activity')), { passive: true })
        }

        const saved = PAGE.exec(location ?? '')
        const page = saved ? Number(saved[1]) : progress > 0 ? Math.round(progress * (this.pageCount - 1)) + 1 : 1
        viewer.currentPageNumber = Math.min(Math.max(1, page), this.pageCount)
        this.#reportLocation()

        return {
            toc: this.#outline.map(item => ({ label: item.label, href: `page:${item.page}`, depth: item.depth })),
            isFixedLayout: true,
            hasPageList: true,
            direction: 'ltr',
        }
    }

    applySettings(settings) {
        this.settings = settings
        const theme = THEMES[settings.theme] ?? THEMES.light
        this.host.style.setProperty('--bw-reader-page', theme.scheme === 'dark' ? '#15110e' : '#e9e1d3')
        this.#container.dataset.theme = settings.theme
        this.#viewer.currentScaleValue = settings.zoom ?? 'page-width'
    }

    zoom(direction) {
        const scale = this.#viewer.currentScale * (direction > 0 ? 1.15 : 1 / 1.15)
        this.#viewer.currentScale = Math.min(5, Math.max(0.25, scale))
        return Math.round(this.#viewer.currentScale * 100)
    }

    next() { this.#viewer.nextPage() }
    prev() { this.#viewer.previousPage() }

    goTo(target) {
        const match = PAGE.exec(target ?? '')
        if (match) this.#viewer.currentPageNumber = Math.min(Math.max(1, Number(match[1])), this.pageCount)
    }

    goToFraction(fraction) {
        this.#viewer.currentPageNumber = Math.round(fraction * (this.pageCount - 1)) + 1
    }

    // Highlights: stored as page + text; drawn over the text layer whenever a page renders.

    setHighlights(list) {
        this.#highlights = list ?? []
        this.#redrawAll()
    }

    addHighlight(highlight) {
        this.#highlights = [...this.#highlights.filter(h => h.id !== highlight.id), highlight]
        this.#drawPage(this.#pageOf(highlight))
    }

    removeHighlight(id) {
        const highlight = this.#highlights.find(h => h.id === id)
        this.#highlights = this.#highlights.filter(h => h.id !== id)
        if (highlight) this.#drawPage(this.#pageOf(highlight))
    }

    #pageOf(highlight) { return Number(PAGE.exec(highlight.location ?? '')?.[1] ?? 0) }

    #redrawAll() {
        for (let page = 1; page <= this.pageCount; page++) this.#drawPage(page)
    }

    #drawPage(pageNumber) {
        const pageView = this.#viewer.getPageView(pageNumber - 1)
        const textLayer = pageView?.textLayer?.div
        if (!pageView?.div) return
        pageView.div.querySelector('.bw-pdf-highlights')?.remove()
        this.#drawn.delete(pageNumber)
        const items = this.#highlights.filter(h => h.state === 'Anchored' && this.#pageOf(h) === pageNumber)
        if (!items.length || !textLayer) return

        const layer = document.createElement('div')
        layer.className = 'bw-pdf-highlights'
        const page = pageView.div.getBoundingClientRect()
        const drawn = []
        for (const highlight of items) {
            const range = findQuote(textLayer, highlight)
            if (!range) continue
            const rects = []
            for (const r of range.getClientRects()) {
                if (r.width < 1 || r.height < 1) continue
                const rect = {
                    left: (r.left - page.left) / page.width,
                    top: (r.top - page.top) / page.height,
                    width: r.width / page.width,
                    height: r.height / page.height,
                }
                rects.push(rect)
                const mark = document.createElement('div')
                mark.style.cssText = `left:${rect.left * 100}%;top:${rect.top * 100}%;width:${rect.width * 100}%;height:${rect.height * 100}%;background:${HIGHLIGHT_COLORS[highlight.color] ?? HIGHLIGHT_COLORS.Yellow}`
                layer.append(mark)
            }
            drawn.push({ id: highlight.id, rects })
        }
        pageView.div.append(layer)
        this.#drawn.set(pageNumber, drawn)
    }

    #onClick(event) {
        if (!document.getSelection()?.isCollapsed) return
        const pageElement = event.target.closest?.('.page')
        if (pageElement && !event.target.closest('a')) {
            const pageNumber = Number(pageElement.dataset.pageNumber)
            const box = pageElement.getBoundingClientRect()
            const x = (event.clientX - box.left) / box.width
            const y = (event.clientY - box.top) / box.height
            for (const { id, rects } of this.#drawn.get(pageNumber) ?? []) {
                const hit = rects.find(r => x >= r.left && x <= r.left + r.width && y >= r.top && y <= r.top + r.height)
                if (hit) {
                    this.#dotnet.invokeMethodAsync('OnHighlightClicked', id, {
                        x: box.left + hit.left * box.width,
                        y: box.top + hit.top * box.height,
                        width: hit.width * box.width,
                        height: hit.height * box.height,
                    })
                    return
                }
            }
        }
        const zone = event.clientX / window.innerWidth
        if (zone > 0.2 && zone < 0.8) this.#dotnet.invokeMethodAsync('OnTap')
    }

    async reanchor(items) {
        const results = []
        for (const item of items) {
            let found = null
            for (let page = 1; page <= this.pageCount && !found; page++) {
                const text = (await this.#pageText(page)).replace(/\s+/g, '')
                if (bestMatch(text, item.text, item.prefix, item.suffix) >= 0) found = page
            }
            results.push(found
                ? { id: item.id, found: true, location: `page:${found}`, chapter: this.#chapterOf(found), pageLabel: this.#labelOf(found), position: this.#positionOf(found) }
                : { id: item.id, found: false })
        }
        return results
    }

    // Search: our own pass over the page texts for the result list, PDF.js's find controller for
    // marking the matches on the pages.

    async search(query) {
        const run = ++this.#searchRun
        const needle = query?.trim().toLocaleLowerCase()
        this.#find(query?.trim() ?? '')
        if (!needle) return
        let count = 0
        for (let page = 1; page <= this.pageCount && count < 500; page++) {
            const text = await this.#pageText(page)
            if (run !== this.#searchRun) return
            const lower = text.toLocaleLowerCase()
            const batch = []
            for (let index = lower.indexOf(needle); index >= 0 && count < 500; index = lower.indexOf(needle, index + needle.length)) {
                const clean = s => s.replace(/\s+/g, ' ')
                batch.push({
                    label: `Page ${this.#labelOf(page)}`,
                    location: `page:${page}`,
                    before: clean(text.slice(Math.max(0, index - 50), index)).trimStart(),
                    match: clean(text.slice(index, index + needle.length)),
                    after: clean(text.slice(index + needle.length, index + needle.length + 50)).trimEnd(),
                })
                count++
            }
            if (batch.length) await this.#dotnet.invokeMethodAsync('OnSearchResults', batch)
            if (page % 10 === 0) await this.#dotnet.invokeMethodAsync('OnSearchProgress', page / this.pageCount)
        }
        if (run === this.#searchRun) await this.#dotnet.invokeMethodAsync('OnSearchDone', count)
    }

    clearSearch() {
        this.#searchRun++
        this.#find('')
    }

    #find(query) {
        this.#lastQuery = query
        this.#eventBus.dispatch('find', {
            source: this,
            type: '',
            query,
            caseSensitive: false,
            entireWord: false,
            highlightAll: true,
            findPrevious: false,
            matchDiacritics: false,
        })
    }

    clearSelection() { document.getSelection()?.removeAllRanges() }

    destroy() {
        this.#searchRun++
        document.removeEventListener('selectionchange', this.#onSelectionChange)
        this.#viewer?.cleanup?.()
        this.#pdf?.loadingTask.destroy()
        this.#container?.remove()
    }

    async #pageText(page) {
        if (!this.#pageTexts.has(page)) {
            const content = await (await this.#pdf.getPage(page)).getTextContent()
            this.#pageTexts.set(page, content.items.map(item => (item.str ?? '') + (item.hasEOL ? '\n' : '')).join(''))
        }
        return this.#pageTexts.get(page)
    }

    #onSelectionChange = debounce(() => this.#checkSelection(), 600)

    #checkSelection() {
        const selection = document.getSelection()
        if (!selection || selection.isCollapsed || !selection.rangeCount) {
            this.#dotnet.invokeMethodAsync('OnSelection', null)
            return
        }
        const range = selection.getRangeAt(0).cloneRange()
        if (!this.#container.contains(range.commonAncestorContainer)) return
        const pageElement = (range.startContainer.nodeType === 1 ? range.startContainer : range.startContainer.parentElement)?.closest('.page')
        const textLayer = pageElement?.querySelector('.textLayer')
        if (!textLayer) return
        // A highlight belongs to one page; a selection running onto the next page is cut at this one's end.
        if (!textLayer.contains(range.endContainer)) range.setEnd(textLayer, textLayer.childNodes.length)
        const text = cleanSelectionText(range.toString())
        if (!text) return
        const pageNumber = Number(pageElement.dataset.pageNumber)
        const rect = range.getBoundingClientRect()
        this.#dotnet.invokeMethodAsync('OnSelection', {
            text,
            location: `page:${pageNumber}`,
            ...contextOf(textLayer, range),
            chapter: this.#chapterOf(pageNumber),
            pageLabel: this.#labelOf(pageNumber),
            position: this.#positionOf(pageNumber),
            rect: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
        })
    }

    #reportLocation() {
        const page = this.#viewer.currentPageNumber
        this.#dotnet.invokeMethodAsync('OnRelocated', {
            location: `page:${page}`,
            progress: this.pageCount > 1 ? (page - 1) / (this.pageCount - 1) : 1,
            chapter: this.#chapterOf(page),
            pageLabel: this.#labels ? this.#labelOf(page) : null,
            pageNumber: page,
            pageCount: this.pageCount,
            atEnd: page >= this.pageCount && this.#atBottom(),
        })
    }

    #atBottom() {
        const c = this.#container
        return c.scrollTop + c.clientHeight >= c.scrollHeight - 40
    }

    #labelOf(page) { return this.#labels?.[page - 1] || String(page) }

    #positionOf(page) { return this.pageCount > 1 ? (page - 1) / (this.pageCount - 1) : 0 }

    #chapterOf(page) {
        let chapter = null
        for (const item of this.#outline) {
            if (item.page <= page && item.depth === 0) chapter = item.label
        }
        return chapter
    }

    async #readOutline() {
        const outline = await this.#pdf.getOutline().catch(() => null)
        const items = []
        const walk = async (nodes, depth) => {
            for (const node of nodes ?? []) {
                let page = null
                try {
                    const dest = typeof node.dest === 'string' ? await this.#pdf.getDestination(node.dest) : node.dest
                    if (Array.isArray(dest)) {
                        page = typeof dest[0] === 'number' ? dest[0] + 1 : (await this.#pdf.getPageIndex(dest[0])) + 1
                    }
                } catch { /* An outline entry without a usable destination. */ }
                if (page) items.push({ label: (node.title ?? '').trim() || `Page ${page}`, page, depth })
                await walk(node.items, depth + 1)
            }
        }
        await walk(outline, 0)
        return items
    }
}
