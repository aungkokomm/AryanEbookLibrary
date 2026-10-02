// Aryan's side of the EPUB reader: opens the book with foliate-js and talks to the app through WebView2
// messages. The app owns everything around the page (toolbar, contents, Define, the reading record); this
// only turns pages, reports where the reader is, and says what was right-clicked.
import { makeBook } from './foliate/view.js'
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
let prefs = { theme: 'paper', fontSize: 100, flow: 'paginated', font: 'book', spacing: 'normal', width: 'medium', justify: false }

// The text choices. Each one's first choice is the look books always had: the book's own font and spacing, 720 wide.
// Windows finds Burmese and Hindi letters in its own fonts (Myanmar Text, Nirmala UI) under either of these.
const FONTS = {
    serif: `Georgia, Cambria, 'Times New Roman', serif`,
    sans: `'Segoe UI', 'Noto Sans', Arial, sans-serif`,
}
const SPACING = { tight: 1.25, wide: 1.85, extra: 2.2 }
const WIDTHS = { narrow: '560px', medium: '720px', wide: '960px' }
let searchHits = []
let searchIndex = -1
let searchRun = 0
let lastActivity = 0
// The book's highlights: where each one is (a CFI) and its colour. The app owns them; this only draws them.
let marks = new Map()
// One outlined for a moment after a jump to it.
let flashing = null

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
    const font = FONTS[prefs.font]
    const spacing = SPACING[prefs.spacing]
    return `
        @namespace epub "http://www.idpf.org/2007/ops";
        html { font-size: ${prefs.fontSize}% !important; }
        p, li, blockquote, dd {
            line-height: 1.5;
            text-align: ${prefs.justify ? 'justify' : 'start'};
            ${prefs.justify ? 'hyphens: auto;' : ''}
            hanging-punctuation: allow-end last;
            widows: 2;
        }
        ${font ? `body, body *:not(code):not(pre):not(kbd):not(samp):not(tt) { font-family: ${font} !important; }` : ''}
        ${spacing ? `body, p, li, blockquote, dd, div { line-height: ${spacing} !important; }` : ''}
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
    // Highlights: on paper and sepia the colour darkens the page under the words and leaves them black; on the dark
    // page that would hide it, so it is laid over a little stronger instead.
    const night = prefs.theme === 'night'
    document.documentElement.style.setProperty('--overlayer-highlight-opacity', night ? '.42' : '.45')
    document.documentElement.style.setProperty('--overlayer-highlight-blend-mode', night ? 'normal' : 'multiply')
    if (!view?.renderer) return
    view.renderer.setAttribute('flow', prefs.flow === 'scrolled' ? 'scrolled' : 'paginated')
    view.renderer.setAttribute('max-column-count', '2')
    view.renderer.setAttribute('max-inline-size', WIDTHS[prefs.width] ?? WIDTHS.medium)
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

const open = async (url, lastLocation, p, list) => {
    prefs = { ...prefs, ...p }
    applyPrefs()
    marks = new Map((list ?? []).map(m => [m.cfi, m.color]))
    view = document.createElement('foliate-view')
    document.body.append(view)
    view.addEventListener('load', e => onSectionLoad(e.detail.doc, e.detail.index))
    view.addEventListener('relocate', e => onRelocate(e.detail))
    view.addEventListener('create-overlay', e => drawSection(e.detail.index))
    view.addEventListener('draw-annotation', e => {
        const { draw, annotation } = e.detail
        const color = marks.get(annotation.value)
        if (color) draw(flashing === annotation.value ? flashDraw : Overlayer.highlight, { color })
    })
    // A click on a highlight: the app shows its colours, note, copy and delete.
    view.addEventListener('show-annotation', e => {
        const { value, range } = e.detail
        if (!marks.has(value)) return
        const doc = range?.startContainer?.ownerDocument
        post({ type: 'mark', cfi: value, rect: doc ? toTop(doc, range.getBoundingClientRect()) : null })
    })
    view.addEventListener('external-link', e => {
        e.preventDefault()
        post({ type: 'external', href: e.detail.href_ })
    })
    try {
        const book = await makeBook(url)
        // A fixed-layout book shows one page at a time, never two side by side, whatever the book asks for.
        if (book.rendition?.layout === 'pre-paginated') book.rendition.spread = 'none'
        await view.open(book)
    } catch (e) {
        post({ type: 'error', message: String(e?.message ?? e) })
        return
    }
    view.renderer.addEventListener('scroll', () => { movedAt = Date.now() })
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

// Keys the app itself answers: the page has the keyboard, so they come from here.
const APP_KEYS = ['F1', 'F3', 'F11', 'Escape']
const APP_CTRL_KEYS = ['f', 'F', 'g', 'G', 'w', 'W', 'd', 'D', '=', '+', '-', '0']

// A fixed-layout book (a Kindle comic) is always shown a page at a time, whatever the layout setting.
const scrolled = () => prefs.flow === 'scrolled' && !view?.isFixedLayout

// Alt pressed and let go on its own shows the app's toolbar and puts the keyboard on it, as a menu bar would.
let altAlone = false

const onKey = e => {
    activity()
    const k = e.key
    const ctrl = e.ctrlKey || e.metaKey
    altAlone = k === 'Alt' && !ctrl && !e.shiftKey
    if (APP_KEYS.includes(k) || (ctrl && APP_CTRL_KEYS.includes(k))) {
        e.preventDefault()
        post({ type: 'key', key: k, ctrl, shift: e.shiftKey })
        return
    }
    if (!view || ctrl) return
    if (e.altKey) {
        // Back and forward after a link or a jump, as in a browser.
        if (k === 'ArrowLeft') view.history.back()
        else if (k === 'ArrowRight') view.history.forward()
        else return
        e.preventDefault()
        return
    }
    if (e.target?.isContentEditable || /input|textarea/i.test(e.target?.tagName ?? '')) return
    // In page layout Down and Up turn the page too. In scroll layout they move a little, and Space and Page Down a
    // screen less a line or two, so the last lines read stay in view.
    const screen = () => scrolled() ? view.renderer.size * 0.9 : undefined
    if (k === 'ArrowLeft') view.goLeft()
    else if (k === 'ArrowRight') view.goRight()
    else if (k === 'ArrowDown') view.next(scrolled() ? 60 : undefined)
    else if (k === 'ArrowUp') view.prev(scrolled() ? 60 : undefined)
    else if (k === 'PageDown' || (k === ' ' && !e.shiftKey)) view.next(screen())
    else if (k === 'PageUp' || (k === ' ' && e.shiftKey)) view.prev(screen())
    else if (k === 'Home') view.goToFraction(0)
    else if (k === 'End') view.goToFraction(1)
    else return
    e.preventDefault()
}

const onKeyUp = e => {
    if (e.key !== 'Alt' || !altAlone) return
    altAlone = false
    e.preventDefault()
    post({ type: 'key', key: 'Alt' })
}

// Alt+Tab away and back: the Alt let go on arrival is not a lone Alt.
const forgetAlt = () => { altAlone = false }

// The app's toolbar can hide above the page and come back when the pointer goes to the top. The page has the
// pointer, so it says when the pointer gets there and when it leaves; a touch near the top is a tap for it.
const TOP_EDGE = 32
let atTop = null

const topOf = (e, frame) => e.clientY + (frame?.getBoundingClientRect().top ?? 0)

const onPointerMove = (e, frame) => {
    activity()
    const near = topOf(e, frame) < TOP_EDGE
    if (near === atTop) return
    atTop = near
    post({ type: 'top', near })
}

// Over the app's toolbar the page hears nothing; back on the page, it says where the pointer is again.
document.documentElement.addEventListener('pointerleave', () => { atTop = null })

const onPointerDown = (e, frame) => {
    altAlone = false
    if (e.pointerType !== 'mouse' && topOf(e, frame) < TOP_EDGE * 2) post({ type: 'tap' })
}

// The mouse's side buttons: back and forward after a link or a jump, not the browser's own.
const onMouseButton = e => {
    if (e.button !== 3 && e.button !== 4) return
    e.preventDefault()
    if (e.type !== 'mouseup' || !view) return
    if (e.button === 3) view.history.back()
    else view.history.forward()
}

// In page layout a click in the margin beside the text turns the page. The text is in the book's own frame, so a
// click that reaches this document is never on it.
const marginSide = x => x < innerWidth * 0.25 ? -1 : x > innerWidth * 0.75 ? 1 : 0

const onMarginClick = e => {
    if (!view || scrolled() || e.button !== 0) return
    const side = marginSide(e.clientX)
    if (side < 0) view.goLeft()
    else if (side > 0) view.goRight()
}

// In page layout (and a fixed-layout book) the wheel turns the page, once per flick: a touchpad sends a stream of
// small steps. In scroll layout it scrolls, and goes on past the end of a section into the next one, or back past its
// start into the end of the one before: foliate shows one section at a time, and the whole book should scroll
// without a button. It waits at the edge a moment first, so a flick that just reached a chapter's end stops there.
const EDGE_WAIT = 300
let wheelAt = 0
let movedAt = 0
const onWheel = e => {
    activity()
    if (!view || e.ctrlKey) return
    const d = Math.abs(e.deltaY) >= Math.abs(e.deltaX) ? e.deltaY : e.deltaX
    const now = Date.now()
    if (Math.abs(d) < 4) return
    if (scrolled()) {
        const r = view.renderer
        const atEdge = d > 0 ? r.viewSize - r.end <= 2 : r.start <= 0
        if (!atEdge || now - movedAt < EDGE_WAIT) return
        movedAt = now
    } else {
        if (now - wheelAt < 350) return
        wheelAt = now
    }
    if (d > 0) view.next()
    else view.prev()
}

document.addEventListener('keydown', onKey)
document.addEventListener('keyup', onKeyUp)
window.addEventListener('blur', forgetAlt)
document.addEventListener('pointermove', e => {
    onPointerMove(e, null)
    if (view) view.style.cursor = !scrolled() && marginSide(e.clientX) ? 'pointer' : ''
})
document.addEventListener('pointerdown', e => onPointerDown(e, null))
document.addEventListener('mousedown', onMouseButton)
document.addEventListener('mouseup', onMouseButton)
document.addEventListener('click', onMarginClick)
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

// ---- highlights ----

// A section just laid out draws the highlights that fall in it.
const drawSection = index => {
    for (const cfi of marks.keys()) {
        try {
            if (view.resolveCFI(cfi).index === index) Promise.resolve(view.addAnnotation({ value: cfi })).catch(() => {})
        } catch { }
    }
}

// The app's list, after any change: gone ones are taken off, the rest drawn again in their colours.
const setMarks = list => {
    const next = new Map((list ?? []).map(m => [m.cfi, m.color]))
    for (const cfi of marks.keys())
        if (!next.has(cfi)) Promise.resolve(view?.deleteAnnotation({ value: cfi })).catch(() => {})
    marks = next
    if (!view?.renderer) return
    for (const cfi of marks.keys()) Promise.resolve(view.addAnnotation({ value: cfi })).catch(() => {})
}

const flashDraw = (rects, options) => {
    const g = document.createElementNS('http://www.w3.org/2000/svg', 'g')
    g.append(Overlayer.highlight(rects, options), Overlayer.outline(rects, { color: '#ff8c00', width: 3 }))
    return g
}

// Goes to a highlight or a note's place; a highlight is outlined for a moment.
const reveal = async cfi => {
    try {
        await view.goTo(cfi)
    } catch {
        return
    }
    if (!marks.has(cfi)) return
    flashing = cfi
    Promise.resolve(view.addAnnotation({ value: cfi })).catch(() => {})
    setTimeout(() => {
        flashing = null
        if (marks.has(cfi)) Promise.resolve(view.addAnnotation({ value: cfi })).catch(() => {})
    }, 1600)
}

// A little of the text either side of a range, for finding it again if its exact place stops fitting.
const around = range => {
    const doc = range.startContainer.ownerDocument
    try {
        const before = doc.createRange()
        before.setStart(doc.body, 0)
        before.setEnd(range.startContainer, range.startOffset)
        const after = doc.createRange()
        after.setStart(range.endContainer, range.endOffset)
        after.setEnd(doc.body, doc.body.childNodes.length)
        return { before: before.toString().slice(-40), after: after.toString().slice(0, 40) }
    } catch {
        return { before: '', after: '' }
    }
}

// Words just selected with the mouse: the app offers the colours over them.
const offerSelection = (doc, index) => {
    const selection = doc.getSelection()
    if (!selection || selection.isCollapsed || !selection.rangeCount) return
    const text = selection.toString().trim()
    if (!text) return
    const range = selection.getRangeAt(0)
    let cfi
    try {
        cfi = view.getCFI(index, range)
    } catch {
        return
    }
    post({ type: 'selection', cfi, text, ...around(range), rect: toTop(doc, range.getBoundingClientRect()) })
}

const onSectionLoad = (doc, index) => {
    const frame = doc.defaultView?.frameElement
    doc.addEventListener('keydown', onKey)
    doc.addEventListener('keyup', onKeyUp)
    doc.defaultView?.addEventListener('blur', forgetAlt)
    doc.addEventListener('pointermove', e => onPointerMove(e, frame))
    doc.addEventListener('pointerdown', e => onPointerDown(e, frame))
    doc.addEventListener('mousedown', onMouseButton)
    doc.addEventListener('mouseup', onMouseButton)
    doc.addEventListener('wheel', onWheel, { passive: true })
    doc.addEventListener('pointerup', e => {
        if (e.pointerType === 'mouse' && e.button === 0) setTimeout(() => offerSelection(doc, index), 0)
    })
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
        let cfi = ''
        if (range) {
            try {
                cfi = view.getCFI(index, range)
            } catch { }
        }
        post({
            type: 'context',
            x: e.clientX + (frame?.left ?? 0),
            y: e.clientY + (frame?.top ?? 0),
            word: word ? word.toString() : '',
            selection: range ? selected : '',
            cfi,
            ...(range ? around(range) : {}),
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

// For checking highlights without a mouse: selects the first place the words are on the page, as a drag would,
// turning a few pages first when they are not there (a book can open at its cover).
const probeSelect = (probe, tries = 8) => {
    // "words+40" goes on 40 characters past the words.
    const [, word, more] = probe.match(/^(.*?)(?:\+(\d+))?$/)
    for (const { doc, index } of view?.renderer?.getContents?.() ?? []) {
        const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT)
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const i = node.textContent.indexOf(word)
            if (i < 0) continue
            const range = doc.createRange()
            range.setStart(node, i)
            range.setEnd(node, Math.min(node.textContent.length, i + word.length + Number(more ?? 0)))
            const r = toTop(doc, range.getBoundingClientRect())
            if (r.x < 0 || r.y < 0 || r.x > innerWidth || r.y > innerHeight) continue
            const selection = doc.getSelection()
            selection.removeAllRanges()
            selection.addRange(range)
            offerSelection(doc, index)
            return
        }
    }
    if (tries > 0) Promise.resolve(view?.next()).then(() => setTimeout(() => probeSelect(probe, tries - 1), 700))
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
            case 'open': await open(m.url, m.lastLocation, m.prefs, m.marks); break
            case 'marks': setMarks(m.marks); break
            case 'reveal': await reveal(m.cfi); break
            case 'prefs': prefs = { ...prefs, ...m.prefs }; applyPrefs(); break
            case 'next': await view?.next(); break
            case 'prev': await view?.prev(); break
            case 'goTo': await view?.goTo(m.href); break
            case 'fraction': await view?.goToFraction(m.fraction); break
            case 'back': view?.history.back(); break
            case 'forward': view?.history.forward(); break
            case 'search': await search(m.query); break
            case 'searchStep': await stepSearch(m.direction); break
            case 'clearSearch': searchRun++; view?.clearSearch(); searchHits = []; searchIndex = -1; view?.deselect(); break
            case 'deselect': view?.deselect(); break
            case 'probe': probe(m.word); break
            case 'probeSelect': probeSelect(m.word); break
        }
    } catch (err) {
        console.error(err)
        if (m.type === 'open') post({ type: 'error', message: String(err?.message ?? err) })
    }
})

post({ type: 'ready' })
