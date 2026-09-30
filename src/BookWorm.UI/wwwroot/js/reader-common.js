// Shared by both reader engines.

export const HIGHLIGHT_COLORS = {
    Yellow: '#f2c544',
    Green: '#7cc47f',
    Blue: '#6fa8dc',
    Pink: '#f08bb0',
    Purple: '#b39ddb',
}

/** Page colours of the reader themes (the app's own light and dark, plus sepia). */
export const THEMES = {
    light: { scheme: 'light', background: '#fffbf4', text: '#2b2118', muted: '#d9cbb6', link: '#9c472e', selection: 'rgba(181, 86, 58, .25)' },
    sepia: { scheme: 'light', background: '#f3e9d2', text: '#5b4636', muted: '#d6c49f', link: '#8a4a2f', selection: 'rgba(138, 74, 47, .25)' },
    dark: { scheme: 'dark', background: '#1e1814', text: '#e6dac8', muted: '#3d3229', link: '#e0915f', selection: 'rgba(224, 145, 95, .35)' },
}

export const debounce = (fn, wait) => {
    let timer
    return (...args) => {
        clearTimeout(timer)
        timer = setTimeout(() => fn(...args), wait)
    }
}

/** A range's box in the coordinates of the top page, also when it is inside a (scaled) frame. */
export const clientRectOf = range => {
    const rect = range.getBoundingClientRect()
    const frame = range.startContainer?.ownerDocument?.defaultView?.frameElement
    if (!frame) return { x: rect.left, y: rect.top, width: rect.width, height: rect.height }
    const box = frame.getBoundingClientRect()
    const scale = frame.offsetWidth ? box.width / frame.offsetWidth : 1
    return {
        x: box.left + rect.left * scale,
        y: box.top + rect.top * scale,
        width: rect.width * scale,
        height: rect.height * scale,
    }
}
