// Aryan's side of the EPUB reader: opens the book with foliate-js and talks to the app through WebView2
// messages. The app owns everything around the page (toolbar, contents, Define, the reading record); this
// only turns pages, reports where the reader is, and says what was right-clicked.
import './foliate/view.js'
import { compare } from './foliate/epubcfi.js'
import { Overlayer } from './foliate/overlayer.js'

const post = msg => window.chrome.webview.postMessage(msg)

const THEMES = {
    paper: { bg: '#ffffff', fg: null, link: null },
    sepia: { bg: '#f4ecd8', fg: '#5b4636', link: '#8a4b08' },
    // Ayaan PDF's night mode: never pure black or pure white, which smear for most readers.
    night: { bg: '#121212', fg: '#ebebeb', link: '#8ab4f8' },
}

let view = null
let prefs = { theme: 'paper', fontSize: 100, flow: 'paginated' }
let searchHits = []
let searchIndex = -1
let searchRun = 0
let lastActivity = 0

// ---- styles ----

const css = () => {
    const t = THEMES[prefs.theme] ?? THEMES.paper
    // Paper keeps the book's own colours. Sepia and night must win over them, or a book that sets black text
    // explicitly turns unreadable on a dark page.
    const colours = t.fg ? `
        html, body { color: ${t.fg} !important; background: ${t.bg} !important; }
        body *:not(img):not(svg):not(video) { color: inherit !important; background-color: transparent !important; border-color: currentColor; }
        a:link, a:visited { color: ${t.link} !important; }
    ` : `
        html { background: ${t.bg}; }
    `
    return `
        @namespace epub "http://www.idpf.org/2007/ops";
        html { font-size: ${prefs.fontSize}% !important; }
        p, li, blockquote, dd {
            line-height: 1.5;
            text-align: start;
            hanging-punctuation: allow-end last;
            widows: 2;
        }
        [align="left"] { text-align: left; }
        [align="right"] { text-align: right; }
        [align="center"] { text-align: center; }
        [align="justify"] { text-align: justify; }
        pre { white-space: pre-wrap !important; }
        aside[epub|type~="endnote"], aside[epub|type~="footnote"],
        aside[epub|type~="note"], aside[epub|type~="rearnote"] { display: none; }
        ${colours}
    `
}

const applyPrefs = () => {
    const t = THEMES[prefs.theme] ?? THEMES.paper
    document.documentElement.style.setProperty('--page-bg', t.bg)
    if (!view?.renderer) return
    view.renderer.setAttribute('flow', prefs.flow === 'scrolled' ? 'scrolled' : 'paginated')
    view.renderer.setAttribute('max-column-count', '2')
    view.renderer.setAttribute('max-inline-size', '720px')
    view.renderer.setAttribute('margin', '40px')
    view.renderer.setStyles?.(css())
}

// ---- opening ----

const flatten = (items, depth = 0, out = []) => {
    for (const item of items ?? []) {
        out.push({ label: (item.label ?? '').trim() || '(untitled)', href: item.href ?? '', depth })
        if (item.subitems?.length) flatten(item.subitems, depth + 1, out)
    }
    return out
}

const text = x => !x ? '' : typeof x === 'string' ? x : x[Object.keys(x)[0]] ?? ''

const open = async (url, lastLocation, p) => {
    prefs = { ...prefs, ...p }
    applyPrefs()
    view = document.createElement('foliate-view')
    document.body.append(view)
    view.addEventListener('load', e => onSectionLoad(e.detail.doc))
    view.addEventListener('relocate', e => onRelocate(e.detail))
    view.addEventListener('external-link', e => {
        e.preventDefault()
        post({ type: 'external', href: e.detail.href_ })
    })
    try {
        await view.open(url)
    } catch (e) {
        post({ type: 'error', message: String(e?.message ?? e) })
        return
    }
    const { book } = view
    book.transformTarget?.addEventListener('data', ({ detail }) => {
        detail.data = Promise.resolve(detail.data).catch(() => '')
    })
    applyPrefs()
    post({
        type: 'opened',
        toc: flatten(book.toc),
        title: text(book.metadata?.title),
        fixed: view.isFixedLayout,
    })
    try {
        await view.init({ lastLocation: lastLocation || null, showTextStart: !lastLocation })
    } catch {
        await view.init({ showTextStart: true })
    }
}

const onRelocate = detail => {
    const { fraction, location, tocItem, pageItem, cfi } = detail
    post({
        type: 'relocate',
        fraction: fraction ?? 0,
        cfi: cfi ?? '',
        current: location?.current ?? 0,
        next: location?.next ?? location?.current ?? 0,
        total: location?.total ?? 0,
        chapter: (tocItem?.label ?? '').trim(),
        page: pageItem?.label ?? '',
    })
    activity(true)
}

// ---- input ----

// The app times reading only while there is input, and the page's input never reaches its window: say so,
// at most every ten seconds.
const activity = force => {
    const now = Date.now()
    if (!force && now - lastActivity < 10000) return
    lastActivity = now
    post({ type: 'activity' })
}

const onKey = e => {
    activity()
    const k = e.key
    const ctrl = e.ctrlKey || e.metaKey
    // Keys the app itself answers: the page has the keyboard, so they come from here.
    if (k === 'F11' || k === 'Escape' || k === 'F3' || (ctrl && ['f', 'F', '=', '+', '-', '0'].includes(k))) {
        e.preventDefault()
        post({ type: 'key', key: k, ctrl, shift: e.shiftKey })
        return
    }
    if (!view || ctrl || e.altKey) return
    if (e.target?.isContentEditable || /input|textarea/i.test(e.target?.tagName ?? '')) return
    if (k === 'ArrowLeft') view.goLeft()
    else if (k === 'ArrowRight') view.goRight()
    else if (k === 'PageDown' || (k === ' ' && !e.shiftKey)) view.next()
    else if (k === 'PageUp' || (k === ' ' && e.shiftKey)) view.prev()
    else if (k === 'Home') view.goToFraction(0)
    else if (k === 'End') view.goToFraction(1)
    else return
    e.preventDefault()
}

// In page layout the wheel turns the page, once per flick: a touchpad sends a stream of small steps.
let wheelAt = 0
const onWheel = e => {
    activity()
    if (!view || prefs.flow === 'scrolled' || e.ctrlKey) return
    const d = Math.abs(e.deltaY) >= Math.abs(e.deltaX) ? e.deltaY : e.deltaX
    const now = Date.now()
    if (Math.abs(d) < 4 || now - wheelAt < 350) return
    wheelAt = now
    if (d > 0) view.next()
    else view.prev()
}

document.addEventListener('keydown', onKey)
document.addEventListener('pointermove', () => activity())
document.addEventListener('wheel', onWheel, { passive: true })

// ---- a section's document: keys, activity, and the right-click ----

const wordRangeAt = (doc, x, y) => {
    const caret = doc.caretRangeFromPoint?.(x, y)
    const node = caret?.startContainer
    if (!node || node.nodeType !== Node.TEXT_NODE) return null
    const offset = caret.startOffset
    const value = node.textContent
    const lang = doc.documentElement.lang || undefined
    let segmenter
    try {
        segmenter = new Intl.Segmenter(lang, { granularity: 'word' })
    } catch {
        segmenter = new Intl.Segmenter(undefined, { granularity: 'word' })
    }
    for (const s of segmenter.segment(value)) {
        if (offset >= s.index && offset <= s.index + s.segment.length) {
            if (!s.isWordLike) return null
            const range = doc.createRange()
            range.setStart(node, s.index)
            range.setEnd(node, s.index + s.segment.length)
            // Only when the pointer is on the word itself, not the space beyond the end of a line.
            const r = range.getBoundingClientRect()
            if (x < r.left - 2 || x > r.right + 2 || y < r.top - 2 || y > r.bottom + 2) return null
            return range
        }
    }
    return null
}

const toTop = (doc, r) => {
    const frame = doc.defaultView?.frameElement?.getBoundingClientRect()
    const dx = frame?.left ?? 0
    const dy = frame?.top ?? 0
    return { x: r.left + dx, y: r.top + dy, width: r.width, height: r.height }
}

const onSectionLoad = doc => {
    doc.addEventListener('keydown', onKey)
    doc.addEventListener('pointermove', () => activity())
    doc.addEventListener('wheel', onWheel, { passive: true })
    doc.addEventListener('contextmenu', e => {
        e.preventDefault()
        activity(true)
        const selection = doc.getSelection()
        const selected = selection?.toString().trim() ?? ''
        let range = null
        if (selected && selection.rangeCount) {
            const r = selection.getRangeAt(0)
            const box = r.getBoundingClientRect()
            if (e.clientX >= box.left && e.clientX <= box.right && e.clientY >= box.top && e.clientY <= box.bottom) range = r
        }
        const word = range ? null : wordRangeAt(doc, e.clientX, e.clientY)
        const anchor = range ?? word
        const frame = doc.defaultView?.frameElement?.getBoundingClientRect()
        post({
            type: 'context',
            x: e.clientX + (frame?.left ?? 0),
            y: e.clientY + (frame?.top ?? 0),
            word: word ? word.toString() : '',
            selection: range ? selected : '',
            rect: anchor ? toTop(doc, anchor.getBoundingClientRect()) : null,
        })
    })
}

// For checking Define without a mouse: where the word first shows on screen, as a right-click would report it.
const probe = word => {
    for (const { doc } of view?.renderer?.getContents?.() ?? []) {
        const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT)
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const i = node.textContent.indexOf(word)
            if (i < 0) continue
            const range = doc.createRange()
            range.setStart(node, i)
            range.setEnd(node, i + word.length)
            const r = toTop(doc, range.getBoundingClientRect())
            if (r.x < 0 || r.y < 0 || r.x > innerWidth || r.y > innerHeight) continue
            post({ type: 'probe', word, rect: r })
            return
        }
    }
    post({ type: 'probe', word, rect: null })
}

// ---- find in book ----

const search = async query => {
    const run = ++searchRun
    view.clearSearch()
    searchHits = []
    searchIndex = -1
    if (!query) {
        post({ type: 'search', count: 0, index: -1, done: true, progress: 1 })
        return
    }
    let lastSent = 0
    const here = view.lastLocation?.cfi
    const after = cfi => {
        try {
            return !here || compare(cfi, here) >= 0
        } catch {
            return true
        }
    }
    // Gold marks, as in the PDF reader; the current match is the selection.
    for await (const result of view.search({ query, draw: Overlayer.highlight, drawOptions: { color: '#ffd700' } })) {
        if (run !== searchRun) return
        if (result === 'done') break
        if (result.subitems) {
            for (const item of result.subitems) searchHits.push(item.cfi)
            // The first match at or after where the reader is, shown as soon as it is found.
            if (searchIndex < 0) {
                const i = searchHits.findIndex(after)
                if (i >= 0) {
                    searchIndex = i
                    await view.select(searchHits[i])
                }
            }
        }
        const now = Date.now()
        if (result.progress != null && now - lastSent > 300) {
            lastSent = now
            post({ type: 'search', count: searchHits.length, index: searchIndex, done: false, progress: result.progress })
        }
    }
    if (run !== searchRun) return
    // Nothing after the reader: round to the first one in the book.
    if (searchIndex < 0 && searchHits.length) {
        searchIndex = 0
        await view.select(searchHits[0])
    }
    post({ type: 'search', count: searchHits.length, index: searchIndex, done: true, progress: 1 })
}

const stepSearch = async direction => {
    if (!searchHits.length) return
    searchIndex = (searchIndex + direction + searchHits.length) % searchHits.length
    await view.select(searchHits[searchIndex])
    post({ type: 'search', count: searchHits.length, index: searchIndex, done: true, progress: 1 })
}

// ---- messages from the app ----

window.chrome.webview.addEventListener('message', async e => {
    const m = e.data
    try {
        switch (m.type) {
            case 'open': await open(m.url, m.lastLocation, m.prefs); break
            case 'prefs': prefs = { ...prefs, ...m.prefs }; applyPrefs(); break
            case 'next': await view?.next(); break
            case 'prev': await view?.prev(); break
            case 'goTo': await view?.goTo(m.href); break
            case 'fraction': await view?.goToFraction(m.fraction); break
            case 'search': await search(m.query); break
            case 'searchStep': await stepSearch(m.direction); break
            case 'clearSearch': searchRun++; view?.clearSearch(); searchHits = []; searchIndex = -1; view?.deselect(); break
            case 'deselect': view?.deselect(); break
            case 'probe': probe(m.word); break
        }
    } catch (err) {
        console.error(err)
        if (m.type === 'open') post({ type: 'error', message: String(err?.message ?? err) })
    }
})

post({ type: 'ready' })
