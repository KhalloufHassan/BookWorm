// Book content runs in frames on BookWorm's own origin, so anything that could execute is
// neutralised before it is shown. Elements are disarmed rather than removed, so the document keeps
// its exact structure and highlight locations (EPUB CFIs) stay valid.

const MARKUP = /(x?html|xml|svg)/i
const URL_ATTRIBUTES = ['href', 'src', 'action', 'formaction', 'data', 'xlink:href']

const isScriptUrl = value => /^\s*(javascript|vbscript|data:text\/html)/i.test(value ?? '')

const disarm = doc => {
    for (const script of doc.getElementsByTagNameNS('*', 'script')) {
        script.setAttribute('type', 'text/plain')
        script.removeAttribute('src')
        script.removeAttributeNS('http://www.w3.org/1999/xlink', 'href')
    }
    for (const element of doc.getElementsByTagName('*')) {
        for (const attribute of Array.from(element.attributes)) {
            const name = attribute.name.toLowerCase()
            if (name.startsWith('on') || name === 'srcdoc'
                || (URL_ATTRIBUTES.includes(name) && isScriptUrl(attribute.value))) {
                element.removeAttributeNode(attribute)
            }
        }
        const tag = element.localName?.toLowerCase()
        if (tag === 'iframe' || tag === 'frame' || tag === 'object' || tag === 'embed') {
            element.removeAttribute('src')
            element.removeAttribute('data')
        }
        else if (tag === 'meta' && element.hasAttribute('http-equiv')) element.removeAttribute('http-equiv')
        else if (tag === 'base') element.removeAttribute('href')
    }
}

const sanitizeMarkup = (text, type) => {
    const html = /text\/html/i.test(type)
    const doc = new DOMParser().parseFromString(text, html ? 'text/html' : type)
    if (!html && doc.getElementsByTagName('parsererror').length) {
        // Not well-formed: at least stop scripts from running.
        return text.replace(/<script\b/gi, '<script type="text/plain"').replace(/\son\w+\s*=/gi, ' data-removed=')
    }
    disarm(doc)
    return html
        ? '<!DOCTYPE html>\n' + doc.documentElement.outerHTML
        : new XMLSerializer().serializeToString(doc)
}

/** Hooks a foliate-js book so its resources are cleaned as they load. */
export const protectBook = book => {
    const target = book.transformTarget
    if (!target) return
    target.addEventListener('load', event => {
        // EPUB script files are never loaded at all.
        if (event.detail.isScript) event.detail.allow = false
    })
    target.addEventListener('data', event => {
        const { detail } = event
        const type = String(detail.type ?? '')
        if (!MARKUP.test(type)) return
        detail.data = Promise.resolve(detail.data)
            .then(data => typeof data === 'string' ? sanitizeMarkup(data, type) : data)
            .catch(() => '')
    })
}
