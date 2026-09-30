// Finding a highlighted passage again from its text, e.g. after the book file was replaced.
// Whitespace is ignored when comparing, because different renderings of the same text (EPUB
// sections, PDF text layers) break lines and join spans differently.

const CONTEXT = 64

const compact = text => (text ?? '').replace(/\s+/g, '')

/** The text nodes under `root`, skipping scripts and styles. */
const textNodes = root => {
    const doc = root.ownerDocument ?? root
    const walker = doc.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: node => {
            const parent = node.parentElement?.localName
            return parent === 'script' || parent === 'style' ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
        },
    })
    const nodes = []
    for (let node = walker.nextNode(); node; node = walker.nextNode()) nodes.push(node)
    return nodes
}

/** The whitespace-free text under `root`, with where each character came from. */
const indexText = root => {
    const nodes = textNodes(root)
    const chars = []
    const nodeOf = []
    const offsetOf = []
    nodes.forEach((node, n) => {
        const value = node.nodeValue
        for (let i = 0; i < value.length; i++) {
            if (!/\s/.test(value[i])) {
                chars.push(value[i])
                nodeOf.push(n)
                offsetOf.push(i)
            }
        }
    })
    return { nodes, text: chars.join(''), nodeOf, offsetOf }
}

const commonSuffix = (a, b) => {
    let n = 0
    while (n < a.length && n < b.length && a[a.length - 1 - n] === b[b.length - 1 - n]) n++
    return n
}

const commonPrefix = (a, b) => {
    let n = 0
    while (n < a.length && n < b.length && a[n] === b[n]) n++
    return n
}

/** Index of the best occurrence of `quote` in `haystack` (all whitespace-free), or -1. */
export const bestMatch = (haystack, quote, prefix, suffix) => {
    const needle = compact(quote)
    if (!needle) return -1
    const before = compact(prefix)
    const after = compact(suffix)
    const search = (hay, ndl, pre, post) => {
        let best = -1
        let bestScore = -1
        for (let i = hay.indexOf(ndl); i >= 0; i = hay.indexOf(ndl, i + 1)) {
            const score = commonSuffix(hay.slice(Math.max(0, i - pre.length), i), pre)
                + commonPrefix(hay.slice(i + ndl.length, i + ndl.length + post.length), post)
            if (score > bestScore) {
                best = i
                bestScore = score
            }
        }
        return best
    }
    const exact = search(haystack, needle, before, after)
    if (exact >= 0) return exact
    return search(haystack.toLowerCase(), needle.toLowerCase(), before.toLowerCase(), after.toLowerCase())
}

/** A DOM range covering the passage under `root`, or null when it isn't there. */
export const findQuote = (root, { text, prefix, suffix }) => {
    const index = indexText(root)
    const start = bestMatch(index.text, text, prefix, suffix)
    if (start < 0) return null
    const end = start + compact(text).length - 1
    const doc = root.ownerDocument ?? root
    const range = doc.createRange()
    range.setStart(index.nodes[index.nodeOf[start]], index.offsetOf[start])
    range.setEnd(index.nodes[index.nodeOf[end]], index.offsetOf[end] + 1)
    return range
}

/** Whether the passage occurs in a plain string (e.g. a PDF page's text). */
export const containsQuote = (text, quote) => bestMatch(compact(text), quote.text, quote.prefix, quote.suffix) >= 0

/** Text just before and after a range, within `root`, to recognise the passage later. */
export const contextOf = (root, range) => {
    const doc = root.ownerDocument ?? root
    const before = doc.createRange()
    before.setStart(root, 0)
    before.setEnd(range.startContainer, range.startOffset)
    const after = doc.createRange()
    after.setStart(range.endContainer, range.endOffset)
    after.setEnd(root, root.childNodes.length)
    const clean = text => text.replace(/\s+/g, ' ')
    return {
        prefix: clean(before.toString()).slice(-CONTEXT).trimStart(),
        suffix: clean(after.toString()).slice(0, CONTEXT).trimEnd(),
    }
}

/** A selection's text with its whitespace tidied, as stored in a highlight. */
export const cleanSelectionText = text => text.replace(/[ \t ]+/g, ' ').replace(/\s*\n\s*/g, '\n').trim()
