// Picking, inspecting and uploading files in the browser. Files stay in the browser (keyed by a
// short id) until they are uploaded, so large books never pass through the WebAssembly app.
import { makeBook } from 'foliate-js/view.js'

const files = new Map()
const urls = new Map()
let counter = 0

const keep = blob => {
    const key = `file-${++counter}`
    files.set(key, blob)
    return key
}

const get = key => {
    const file = files.get(key)
    if (!file) throw new Error('The chosen file is no longer available. Choose it again.')
    return file
}

/** Opens the file picker. Resolves with the chosen file's details, or null when cancelled. */
export function pickFile(accept) {
    return new Promise(resolve => {
        const input = document.createElement('input')
        input.type = 'file'
        input.accept = accept ?? ''
        input.style.display = 'none'
        document.body.append(input)
        const done = result => {
            input.remove()
            resolve(result)
        }
        input.addEventListener('change', () => {
            const file = input.files?.[0]
            done(file ? { key: keep(file), name: file.name, size: file.size, type: file.type } : null)
        })
        input.addEventListener('cancel', () => done(null))
        input.click()
    })
}

export function objectUrl(key) {
    if (!urls.has(key)) urls.set(key, URL.createObjectURL(get(key)))
    return urls.get(key)
}

export function release(key) {
    if (urls.has(key)) URL.revokeObjectURL(urls.get(key))
    urls.delete(key)
    files.delete(key)
}

// Metadata

const text = value => {
    if (!value) return ''
    if (typeof value === 'string') return value
    if (typeof value === 'object') return text(value.name ?? Object.values(value)[0])
    return String(value)
}

const people = value => {
    const list = Array.isArray(value) ? value : value ? [value] : []
    return list
        .flatMap(person => text(person).split(/\s*[;&]\s*/))
        .map(name => name.replace(/\s+/g, ' ').trim())
        .filter((name, index, all) => name && all.indexOf(name) === index)
}

/** Only complete dates are used; "2020" alone isn't enough for a date field. */
const fullDate = value => {
    const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(text(value).trim())
    return match ? `${match[1]}-${match[2]}-${match[3]}` : null
}

const titleFromFileName = name => name
    .replace(/\.(fb2\.zip|[a-z0-9]+)$/i, '')
    .replace(/[_]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()

/**
 * Reads title, authors, publication date and cover from an ebook file. The cover, if any, is
 * scaled down and kept as another file key.
 */
export async function readMetadata(key, format) {
    const file = get(key)
    const result = { title: '', authors: [], published: null, publishedText: null, language: null, coverKey: null }
    try {
        if (format === 'Pdf') await readPdf(file, result)
        else await readEbook(file, result)
    } catch (error) {
        console.warn('Could not read the book metadata', error)
    }
    // Comics usually have no title of their own; foliate-js then reports the file name.
    if (!result.title?.trim() || result.title === file.name) result.title = titleFromFileName(file.name)
    return result
}

async function readEbook(file, result) {
    const book = await makeBook(file)
    const metadata = book.metadata ?? {}
    result.title = text(metadata.title)
    result.authors = people(metadata.author)
    result.published = fullDate(metadata.published)
    result.publishedText = text(metadata.published) || null
    result.language = text(metadata.language) || null
    const cover = await book.getCover?.()
    if (cover) result.coverKey = await shrink(cover)
}

async function readPdf(file, result) {
    const { pdfjsLib, PDF_OPTIONS } = await import('./reader-pdf.js')
    const pdf = await pdfjsLib.getDocument({ data: new Uint8Array(await file.arrayBuffer()), ...PDF_OPTIONS }).promise
    try {
        const { info } = await pdf.getMetadata()
        result.title = text(info?.Title)
        result.authors = people(info?.Author)
        const page = await pdf.getPage(1)
        const viewport = page.getViewport({ scale: 1 })
        const scale = Math.min(2, 600 / viewport.width)
        const canvas = document.createElement('canvas')
        const scaled = page.getViewport({ scale })
        canvas.width = Math.round(scaled.width)
        canvas.height = Math.round(scaled.height)
        await page.render({ canvas, canvasContext: canvas.getContext('2d'), viewport: scaled }).promise
        result.coverKey = keep(await toJpeg(canvas))
    } finally {
        await pdf.destroy()
    }
}

/** The cover of a book file already in the library (for "use the cover from the file"). */
export async function coverFromUrl(url, fileName, format) {
    const response = await fetch(url, { credentials: 'same-origin' })
    if (!response.ok) throw new Error(`The book file couldn't be downloaded (${response.status}).`)
    const key = keep(new File([await response.blob()], fileName))
    try {
        const metadata = await readMetadata(key, format)
        return metadata.coverKey
    } finally {
        release(key)
    }
}

// Images

const toJpeg = canvas => new Promise((resolve, reject) =>
    canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('The image could not be converted.')), 'image/jpeg', 0.85))

/** Covers are shown at most ~450 pixels wide (on high-density screens), so 600×900 is plenty. */
const shrink = async (blob, maxWidth = 600, maxHeight = 900) => {
    const bitmap = await createImageBitmap(blob)
    try {
        const scale = Math.min(1, maxWidth / bitmap.width, maxHeight / bitmap.height)
        const canvas = document.createElement('canvas')
        canvas.width = Math.max(1, Math.round(bitmap.width * scale))
        canvas.height = Math.max(1, Math.round(bitmap.height * scale))
        const context = canvas.getContext('2d')
        context.fillStyle = '#fff'
        context.fillRect(0, 0, canvas.width, canvas.height)
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
        return keep(await toJpeg(canvas))
    } finally {
        bitmap.close()
    }
}

/** A scaled-down JPEG copy of an image, for covers. */
export async function prepareCover(key) {
    return shrink(get(key))
}

// Uploads

/**
 * Sends a kept file as the raw body of a PUT request, reporting progress to `dotnet.OnUploadProgress`.
 * Sends the session cookie, or the given Authorization header (the mobile app, calling the server
 * directly). Resolves with the status code and response text; rejects only on network errors.
 */
export function upload(key, url, dotnet, authorization) {
    const file = get(key)
    return new Promise((resolve, reject) => {
        const request = new XMLHttpRequest()
        request.open('PUT', url)
        if (authorization) request.setRequestHeader('Authorization', authorization)
        else request.withCredentials = true
        request.setRequestHeader('Content-Type', file.type || 'application/octet-stream')
        let last = 0
        request.upload.addEventListener('progress', event => {
            const now = Date.now()
            if (dotnet && event.lengthComputable && (now - last > 200 || event.loaded === event.total)) {
                last = now
                dotnet.invokeMethodAsync('OnUploadProgress', event.loaded, event.total)
            }
        })
        request.addEventListener('load', () => resolve({ status: request.status, body: request.responseText }))
        request.addEventListener('error', () => reject(new Error('The upload failed. Check your connection and try again.')))
        request.addEventListener('abort', () => reject(new Error('The upload was cancelled.')))
        request.send(file)
    })
}
