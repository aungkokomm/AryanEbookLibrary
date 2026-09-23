//! The reading half of Ayaan PDF's render core (E:\PdfEditor\render_core, MIT), copied at Ayaan 3.51.2
//! (cae8c5f) and cut down to what a reader needs: open a document, measure its pages, render tiles of
//! them, read a page's characters, the outline and the links. Nothing here writes to a document.
//!
//! The C ABI is kept the same as Ayaan's for every function that came across, so a fix made there can
//! be carried over by hand without translating it.
//!
//! PDFium itself is not thread-safe, and pdfium-render's `thread_safe` feature only locks library
//! init and teardown; every other call goes straight through. `CALL_LOCK` is this crate's own
//! serialization of every native-touching call, and it is the actual thread-safety mechanism. Two
//! unlocked concurrent renders reliably corrupt the heap (Ayaan caught it with parallel tests).

use std::collections::HashMap;
use std::ffi::CStr;
use std::num::NonZeroUsize;
use std::os::raw::c_char;
use std::panic;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use lru::LruCache;
use pdfium_render::prelude::{Pdfium, PdfDocument, PdfiumError, PdfiumInternalError};

// ---------------------------------------------------------------------
// Statuses and FFI result types
// ---------------------------------------------------------------------

pub const STATUS_OK_PDFIUM: i32 = 0;
pub const STATUS_INVALID_INPUT: i32 = 1;
pub const STATUS_PANIC: i32 = 2;

/// The file is a real PDF but is encrypted, and the password given (if any) did not open it.
/// Reported the same way for "no password" and "wrong password": PDFium does not tell them apart.
pub const STATUS_NEEDS_PASSWORD: i32 = 5;

#[repr(C)]
pub struct RenderResult {
    pub width: i32,
    pub height: i32,
    /// A heap-allocated BGRA8 buffer of `len` bytes (width * height * 4), null on failure.
    /// Ownership passes to the caller; release it with `free_render_result`.
    pub buffer: *mut u8,
    pub len: usize,
    pub status: i32,
}

impl RenderResult {
    fn failure(status: i32) -> Self {
        RenderResult { width: 0, height: 0, buffer: std::ptr::null_mut(), len: 0, status }
    }
}

/// Frees a buffer returned in a `RenderResult`. A failed result (null buffer) is a no-op.
#[unsafe(no_mangle)]
pub extern "C" fn free_render_result(result: RenderResult) {
    if result.buffer.is_null() || result.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(result.buffer, result.len));
    }
}

/// Bytes handed to the caller (outline, links). Release with `free_byte_buffer`.
#[repr(C)]
pub struct ByteBuffer {
    pub data: *mut u8,
    pub len: usize,
    pub status: i32,
}

impl ByteBuffer {
    fn err(status: i32) -> Self {
        ByteBuffer { data: std::ptr::null_mut(), len: 0, status }
    }

    fn ok(out: Vec<u8>) -> Self {
        let mut boxed = out.into_boxed_slice();
        let buffer = ByteBuffer { data: boxed.as_mut_ptr(), len: boxed.len(), status: STATUS_OK_PDFIUM };
        std::mem::forget(boxed);
        buffer
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn free_byte_buffer(buffer: ByteBuffer) {
    if buffer.data.is_null() || buffer.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(buffer.data, buffer.len));
    }
}

// ---------------------------------------------------------------------
// Global state: one Pdfium instance, open documents, the tile cache.
// ---------------------------------------------------------------------

static PDFIUM: OnceLock<Option<Pdfium>> = OnceLock::new();

/// Every call that touches PDFium must hold this for the whole call. See the module docs.
static CALL_LOCK: Mutex<()> = Mutex::new(());

/// Locks a mutex, recovering from poisoning instead of panicking. The FFI entry points catch panics,
/// so one panic must not leave every shared map poisoned and every later call failing.
fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn call_guard() -> std::sync::MutexGuard<'static, ()> {
    lock(&CALL_LOCK)
}

/// Binds PDFium once, from the host executable's own folder (the working directory cannot be trusted),
/// and reuses that instance for the life of the process. `None` means PDFium is not available.
fn pdfium() -> Option<&'static Pdfium> {
    PDFIUM
        .get_or_init(|| {
            let exe_dir = std::env::current_exe().ok()?.parent()?.to_path_buf();
            Pdfium::bind_to_library(Pdfium::pdfium_platform_library_name_at_path(
                exe_dir.to_string_lossy().as_ref(),
            ))
            .or_else(|_| Pdfium::bind_to_system_library())
            .ok()
            .map(Pdfium::new)
        })
        .as_ref()
}

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
struct TileKey {
    doc: u64,
    page: i32,
    level: i32,
    col: i32,
    row: i32,
}

#[derive(Clone)]
struct CachedTile {
    width: i32,
    height: i32,
    bytes: Arc<[u8]>,
}

/// Total pixel bytes the tile cache may hold. Evicting on bytes, not on an entry count, is what keeps
/// memory bounded at any zoom.
const CACHE_BUDGET_BYTES: usize = 320 * 1024 * 1024;

fn cache_put(key: TileKey, tile: CachedTile) {
    let mut cache = lock(&core().cache);
    cache.put(key, tile);

    let mut total: usize = cache.iter().map(|(_, t)| t.bytes.len()).sum();
    while total > CACHE_BUDGET_BYTES {
        match cache.pop_lru() {
            Some((_, evicted)) => total -= evicted.bytes.len(),
            None => break,
        }
    }
}

struct Core {
    /// The inner Mutex is for Rust's borrow rules, not thread safety: CALL_LOCK already serializes
    /// every native call.
    documents: Mutex<HashMap<u64, Arc<Mutex<PdfDocument<'static>>>>>,
    next_doc_id: AtomicU64,
    cache: Mutex<LruCache<TileKey, CachedTile>>,
}

static CORE: OnceLock<Core> = OnceLock::new();

fn core() -> &'static Core {
    CORE.get_or_init(|| Core {
        documents: Mutex::new(HashMap::new()),
        next_doc_id: AtomicU64::new(1),
        // Eviction is by bytes in cache_put; the count is only a backstop.
        cache: Mutex::new(LruCache::new(NonZeroUsize::new(1024).unwrap())),
    })
}

fn document(handle: u64) -> Option<Arc<Mutex<PdfDocument<'static>>>> {
    lock(&core().documents).get(&handle).cloned()
}

// ---------------------------------------------------------------------
// Document handles
// ---------------------------------------------------------------------

/// The outcome of trying to open a document: the handle, and why not.
#[repr(C)]
pub struct OpenResult {
    /// Non-zero on success, zero on every failure.
    pub handle: u64,
    /// STATUS_OK_PDFIUM, STATUS_NEEDS_PASSWORD, STATUS_INVALID_INPUT or STATUS_PANIC.
    pub status: i32,
}

/// Opens a document, optionally with a password (null or empty = none, which also opens the common
/// file that is encrypted with an empty user password to restrict printing or copying).
#[unsafe(no_mangle)]
pub extern "C" fn open_document_protected(path: *const c_char, password: *const c_char) -> OpenResult {
    panic::catch_unwind(|| open_protected_inner(path, password))
        .unwrap_or(OpenResult { handle: 0, status: STATUS_PANIC })
}

fn open_protected_inner(path: *const c_char, password: *const c_char) -> OpenResult {
    let failed = |status| OpenResult { handle: 0, status };

    if path.is_null() {
        return failed(STATUS_INVALID_INPUT);
    }
    let Ok(path_str) = (unsafe { CStr::from_ptr(path) }).to_str() else {
        return failed(STATUS_INVALID_INPUT);
    };

    // An empty password is the same as none. Passing "" through is not: PDFium treats it as an
    // attempt and fails a document that would have opened unprotected.
    let password_str = if password.is_null() {
        None
    } else {
        match (unsafe { CStr::from_ptr(password) }).to_str() {
            Ok("") => None,
            Ok(text) => Some(text),
            Err(_) => return failed(STATUS_INVALID_INPUT),
        }
    };

    let document = {
        let _guard = call_guard();
        let Some(pdfium) = pdfium() else {
            return failed(STATUS_INVALID_INPUT);
        };

        match pdfium.load_pdf_from_file(path_str, password_str) {
            Ok(document) => document,
            Err(PdfiumError::PdfiumLibraryInternalError(PdfiumInternalError::PasswordError)) => {
                return failed(STATUS_NEEDS_PASSWORD);
            }
            Err(_) => return failed(STATUS_INVALID_INPUT),
        }
    };

    let core = core();
    let id = core.next_doc_id.fetch_add(1, Ordering::Relaxed);
    lock(&core.documents).insert(id, Arc::new(Mutex::new(document)));

    OpenResult { handle: id, status: STATUS_OK_PDFIUM }
}

/// Closes a document and drops its cached tiles. A no-op for an unknown or zero handle.
#[unsafe(no_mangle)]
pub extern "C" fn close_document(doc_handle: u64) {
    if doc_handle == 0 {
        return;
    }
    let core = core();

    // Dropping the last Arc runs PDFium's native close, so it needs the lock like any other call.
    {
        let _guard = call_guard();
        let removed = lock(&core.documents).remove(&doc_handle);
        drop(removed);
    }

    let mut cache = lock(&core.cache);
    let stale: Vec<TileKey> = cache.iter().filter(|(k, _)| k.doc == doc_handle).map(|(k, _)| *k).collect();
    for key in stale {
        cache.pop(&key);
    }
}

/// The page count, or -1 for an unknown handle.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_count(doc_handle: u64) -> i32 {
    let _guard = call_guard();
    match document(doc_handle) {
        Some(doc) => lock(&doc).pages().len() as i32,
        None => -1,
    }
}

// ---------------------------------------------------------------------
// Page sizes
// ---------------------------------------------------------------------

/// One page's size in PDF points.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct PageSize {
    pub width: f32,
    pub height: f32,
}

/// Every page's size. Release with `free_page_size_array`.
#[repr(C)]
pub struct PageSizeArray {
    pub sizes: *mut PageSize,
    pub len: usize,
    pub status: i32,
}

impl PageSizeArray {
    fn failure(status: i32) -> Self {
        PageSizeArray { sizes: std::ptr::null_mut(), len: 0, status }
    }
}

/// Every page's size in one locked pass. A continuous page stack has to lay out every page before it
/// draws any of them, and asking page by page would take the lock once per page.
///
/// ⚠️ A WHOLE-DOCUMENT CALL. About a second on a 40,000-page book: call it once when the book opens,
/// never per scroll step (Ayaan's 40-second scroll freeze was exactly that).
#[unsafe(no_mangle)]
pub extern "C" fn get_page_sizes(doc_handle: u64) -> PageSizeArray {
    if doc_handle == 0 {
        return PageSizeArray::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_page_sizes_inner(doc_handle)).unwrap_or_else(|_| PageSizeArray::failure(STATUS_PANIC))
}

fn get_page_sizes_inner(doc_handle: u64) -> PageSizeArray {
    let _guard = call_guard();
    let Some(doc) = document(doc_handle) else {
        return PageSizeArray::failure(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);

    let pages = doc_guard.pages();
    let count = pages.len();
    let mut out: Vec<PageSize> = Vec::with_capacity(count as usize);
    for i in 0..count {
        // page_size(), NOT get(): get() LOADS and parses the page just to read two numbers. On a
        // 3352-page book that was 37.8 seconds against almost nothing.
        match pages.page_size(i) {
            Ok(rect) => out.push(PageSize { width: rect.width().value, height: rect.height().value }),
            // Index-aligned with the pages even when one fails; a zero size is a slot to skip.
            Err(_) => out.push(PageSize { width: 0.0, height: 0.0 }),
        }
    }

    let mut boxed = out.into_boxed_slice();
    let array = PageSizeArray { sizes: boxed.as_mut_ptr(), len: boxed.len(), status: STATUS_OK_PDFIUM };
    std::mem::forget(boxed);
    array
}

#[unsafe(no_mangle)]
pub extern "C" fn free_page_size_array(array: PageSizeArray) {
    if array.sizes.is_null() || array.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(array.sizes, array.len));
    }
}

// ---------------------------------------------------------------------
// Tiles
// ---------------------------------------------------------------------

/// Pixel edge of one tile. MUST equal TileGrid.TileSize in the app: both sides work out the same
/// pyramid from it independently.
pub const TILE_SIZE: i32 = 512;

/// Renders one tile of a page's level-of-detail pyramid, with caching.
///
/// At `level` the page is `TILE_SIZE * 2^level` pixels wide and `2^level` tiles across; the page's
/// aspect decides how many rows there are, so tiles stay square in pixels. Panning reuses every tile
/// that stays on screen, and a coarser level is always there to show while a finer one renders.
#[unsafe(no_mangle)]
pub extern "C" fn render_tile(doc_handle: u64, page_index: i32, level: i32, col: i32, row: i32) -> RenderResult {
    if doc_handle == 0 || page_index < 0 || level < 0 || level > 20 || col < 0 || row < 0 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| render_tile_inner(doc_handle, page_index, level, col, row))
        .unwrap_or_else(|_| RenderResult::failure(STATUS_PANIC))
}

fn render_tile_inner(doc_handle: u64, page_index: i32, level: i32, col: i32, row: i32) -> RenderResult {
    let key = TileKey { doc: doc_handle, page: page_index, level, col, row };

    if let Some(tile) = lock(&core().cache).get(&key) {
        return tile_to_result(tile);
    }

    let across = (1i64 << level) as f32;

    let aspect = {
        let _guard = call_guard();
        let Some(doc) = document(doc_handle) else {
            return RenderResult::failure(STATUS_INVALID_INPUT);
        };
        let g = lock(&doc);
        let Ok(page) = g.pages().get(page_index as u16) else {
            return RenderResult::failure(STATUS_INVALID_INPUT);
        };
        let w = page.width().value;
        if w <= 0.0 {
            return RenderResult::failure(STATUS_INVALID_INPUT);
        }
        page.height().value / w
    };

    let rows_down = (across * aspect).ceil();
    if (col as f32) >= across || (row as f32) >= rows_down {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }

    // The tile as fractions of the page's width and height. The vertical divisor carries the aspect so
    // the tile stays SQUARE in pixels.
    let x = col as f32 / across;
    let w = 1.0 / across;
    let y = row as f32 / (across * aspect);
    let h = 1.0 / (across * aspect);

    let Some((width, height, bytes)) = render_region(doc_handle, page_index, x, y, w, h, TILE_SIZE) else {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    };

    let tile = CachedTile { width, height, bytes: Arc::from(bytes) };
    let out = tile_to_result(&tile);
    cache_put(key, tile);
    out
}

/// Renders a whole page `width` pixels wide, with caching (keyed apart from the tiles by level -1).
///
/// Aryan's reader uses this at ordinary reading sizes and tiles only for deep zoom. A tile is a separate
/// render of the page, and on a scanned book each one decodes the page's whole picture again: a page two
/// thousand pixels wide is 24 tiles, and 24 decodes where one would do.
#[unsafe(no_mangle)]
pub extern "C" fn render_page_width(doc_handle: u64, page_index: i32, width: i32) -> RenderResult {
    if doc_handle == 0 || page_index < 0 || width <= 0 || width > 16_384 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| render_page_width_inner(doc_handle, page_index, width))
        .unwrap_or_else(|_| RenderResult::failure(STATUS_PANIC))
}

fn render_page_width_inner(doc_handle: u64, page_index: i32, width: i32) -> RenderResult {
    let key = TileKey { doc: doc_handle, page: page_index, level: -1, col: width, row: -1 };
    if let Some(tile) = lock(&core().cache).get(&key) {
        return tile_to_result(tile);
    }

    let rendered = {
        // Held across the drop of our Arc clone too: dropping the last reference closes the document.
        let _guard = call_guard();
        let doc = document(doc_handle);
        let result = doc.as_ref().and_then(|d| {
            let guard = lock(d);
            let page = guard.pages().get(page_index as u16).ok()?;
            render_page(&page, width)
        });
        drop(doc);
        result
    };

    let Some((w, h, bytes)) = rendered else {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    };
    let tile = CachedTile { width: w, height: h, bytes: Arc::from(bytes) };
    let out = tile_to_result(&tile);
    cache_put(key, tile);
    out
}

fn tile_to_result(tile: &CachedTile) -> RenderResult {
    buffer_to_result(tile.width, tile.height, tile.bytes.to_vec())
}

/// Renders a rectangle of a page (fractions of its width and height, top-left origin) at `out_width`
/// pixels, by narrowing the page's crop box for the length of the render.
fn render_region(doc_handle: u64, page_index: i32, x: f32, y: f32, w: f32, h: f32, out_width: i32)
    -> Option<(i32, i32, Vec<u8>)>
{
    use pdfium_render::prelude::*;

    let _guard = call_guard();
    let doc = document(doc_handle)?;
    let doc_guard = lock(&doc);
    let mut page = doc_guard.pages().get(page_index as u16).ok()?;

    let page_w = page.width().value;
    let page_h = page.height().value;
    if page_w <= 0.0 || page_h <= 0.0 {
        return None;
    }

    // The box to restore.
    let original = page_box(&page);

    // Fractions, top-left origin -> PDF points, bottom-left origin.
    let left = original.left().value + x * page_w;
    let right = left + w * page_w;
    let top = original.top().value - y * page_h;
    let bottom = top - h * page_h;
    let region = PdfRect::new(PdfPoints::new(bottom), PdfPoints::new(left), PdfPoints::new(top), PdfPoints::new(right));

    if page.boundaries_mut().set_crop(region).is_err() {
        return None;
    }

    let rendered = render_page(&page, out_width);

    // ALWAYS restore, including after a failed render: page sizes are derived from this box.
    let _ = page.boundaries_mut().set_crop(original);

    rendered
}

/// Rasterizes a page to BGRA, the byte order a WinUI WriteableBitmap takes.
///
/// ⚠️ .set_reverse_byte_order(false)`: PdfRenderConfig defaults to TRUE, which makes PDFium write RGBA
/// while the bitmap still reports BGRA, and swaps red and blue on every coloured page. It is invisible
/// on black-on-white pages, which is how it survived in Ayaan for a long time.
fn render_page(page: &pdfium_render::prelude::PdfPage<'_>, target_width: i32) -> Option<(i32, i32, Vec<u8>)> {
    use pdfium_render::prelude::*;

    let config = PdfRenderConfig::new()
        .set_target_width(target_width)
        .set_reverse_byte_order(false)
        .rotate_if_landscape(PdfPageRenderRotation::None, false);

    let bitmap = page.render_with_config(&config).ok()?;
    let width = bitmap.width() as i32;
    let height = bitmap.height() as i32;
    Some((width, height, bitmap.as_raw_bytes()))
}

fn buffer_to_result(width: i32, height: i32, buf: Vec<u8>) -> RenderResult {
    let mut boxed = buf.into_boxed_slice();
    let len = boxed.len();
    let ptr = boxed.as_mut_ptr();
    std::mem::forget(boxed);
    RenderResult { width: width.max(1), height: height.max(1), buffer: ptr, len, status: STATUS_OK_PDFIUM }
}

// ---------------------------------------------------------------------
// Characters
// ---------------------------------------------------------------------

#[repr(C)]
pub struct CharInfo {
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
    /// The character's Unicode scalar value (0 when PDFium reports something that is not one).
    pub codepoint: u32,
}

#[repr(C)]
pub struct CharInfoArray {
    pub chars: *mut CharInfo,
    pub len: usize,
    pub status: i32,
}

impl CharInfoArray {
    fn failure(status: i32) -> Self {
        CharInfoArray { chars: std::ptr::null_mut(), len: 0, status }
    }
}

/// Every character on a page with its box, in pixels of a render `target_width` wide, top-left origin.
/// A page with no text (a scan) is an empty success.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_chars(doc_handle: u64, page_index: i32, target_width: i32) -> CharInfoArray {
    if doc_handle == 0 || page_index < 0 || target_width <= 0 {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_page_chars_inner(doc_handle, page_index, target_width))
        .unwrap_or_else(|_| CharInfoArray::failure(STATUS_PANIC))
}

fn get_page_chars_inner(doc_handle: u64, page_index: i32, target_width: i32) -> CharInfoArray {
    let _guard = call_guard();
    let Some(doc) = document(doc_handle) else {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);
    let result = extract_char_infos(&doc_guard, page_index, target_width);
    drop(doc_guard);
    drop(doc);

    let Some(infos) = result else {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    };

    let mut boxed = infos.into_boxed_slice();
    let len = boxed.len();
    let ptr = boxed.as_mut_ptr();
    std::mem::forget(boxed);
    CharInfoArray { chars: ptr, len, status: STATUS_OK_PDFIUM }
}

/// Caller must hold `CALL_LOCK`.
fn extract_char_infos(doc: &PdfDocument<'static>, page_index: i32, target_width: i32) -> Option<Vec<CharInfo>> {
    let page = doc.pages().get(page_index as u16).ok()?;
    let page_height = page.height().value;
    let scale = target_width as f32 / page.width().value;

    let Ok(text) = page.text() else {
        return Some(Vec::new());
    };
    let chars = text.chars();

    let mut infos = Vec::with_capacity(chars.len() as usize);
    for c in chars.iter() {
        // loose_bounds (the font's whole glyph cell), not tight_bounds (the ink): tight boxes differ per
        // glyph ('o' and 'f' on one line), which breaks grouping characters into lines by their top.
        let Ok(bounds) = c.loose_bounds() else { continue };
        infos.push(CharInfo {
            left: bounds.left().value * scale,
            top: (page_height - bounds.top().value) * scale,
            right: bounds.right().value * scale,
            bottom: (page_height - bounds.bottom().value) * scale,
            codepoint: c.unicode_value(),
        });
    }

    Some(infos)
}

#[unsafe(no_mangle)]
pub extern "C" fn free_char_info_array(array: CharInfoArray) {
    if array.chars.is_null() || array.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(array.chars, array.len));
    }
}

// ---------------------------------------------------------------------
// Outline (the document's own bookmarks)
//
// Little-endian: u32 count, then per entry i32 depth, i32 page_index, u32 title_len, title (UTF-8).
// Flat, in reading order, with a depth on each entry. page_index is -1 when the entry goes nowhere in
// this document (a web address, a named destination the file never defines); it still shows, because
// it is part of the author's outline.
// ---------------------------------------------------------------------

/// A malformed outline can be a cycle, and PDFium will walk one for ever, so the walk is bounded.
const MAX_BOOKMARKS: u32 = 20_000;
const MAX_BOOKMARK_DEPTH: i32 = 32;

#[unsafe(no_mangle)]
pub extern "C" fn get_bookmarks(doc_handle: u64) -> ByteBuffer {
    if doc_handle == 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_bookmarks_inner(doc_handle)).unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_bookmarks_inner(doc_handle: u64) -> ByteBuffer {
    use pdfium_render::prelude::*;

    let _guard = call_guard();
    let Some(doc) = document(doc_handle) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);

    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(&0u32.to_le_bytes()); // count, filled in below
    let mut count: u32 = 0;

    // Every call here is catalog-level: nothing LOADS a page, which keeps a big book's outline cheap.
    fn walk(node: Option<PdfBookmark<'_>>, depth: i32, out: &mut Vec<u8>, count: &mut u32) {
        let mut current = node;
        while let Some(bookmark) = current {
            if *count >= MAX_BOOKMARKS {
                return;
            }

            let title = bookmark.title().unwrap_or_default();

            // A destination directly, or the one inside a GoTo action: plenty of real files use the
            // action form. The action is bound to a local because its destination borrows from it.
            let page_index = if let Some(dest) = bookmark.destination() {
                dest.page_index().map(|i| i as i32).unwrap_or(-1)
            } else if let Some(action) = bookmark.action() {
                action
                    .as_local_destination_action()
                    .and_then(|a| a.destination().ok())
                    .and_then(|d| d.page_index().ok())
                    .map(|i| i as i32)
                    .unwrap_or(-1)
            } else {
                -1
            };

            out.extend_from_slice(&depth.to_le_bytes());
            out.extend_from_slice(&page_index.to_le_bytes());
            out.extend_from_slice(&(title.len() as u32).to_le_bytes());
            out.extend_from_slice(title.as_bytes());
            *count += 1;

            if depth < MAX_BOOKMARK_DEPTH {
                walk(bookmark.first_child(), depth + 1, out, count);
            }

            current = bookmark.next_sibling();
        }
    }

    walk(doc_guard.bookmarks().root(), 0, &mut out, &mut count);
    out[0..4].copy_from_slice(&count.to_le_bytes());
    ByteBuffer::ok(out)
}

// ---------------------------------------------------------------------
// Links
// ---------------------------------------------------------------------

/// A link whose action opens a web address.
pub const LINK_URI: u32 = 0;
/// A link that jumps somewhere inside this document.
pub const LINK_INTERNAL: u32 = 1;
/// A link carrying neither (a launch action, an embedded file, nothing at all).
pub const LINK_OTHER: u32 = 2;

struct LinkInfo {
    index: usize,
    kind: u32,
    uri: String,
    target_page: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
}

/// The page's top-left corner in PDF points, from the same box rendering uses.
fn page_origin(page: &pdfium_render::prelude::PdfPage) -> (f32, f32) {
    let b = page_box(page);
    (b.left().value, b.top().value)
}

/// The box a page is drawn from: its crop box, else its media box, else the box PDFium works out itself.
///
/// ⚠️ THE LAST STEP IS NOT A NICETY. Many real books put /MediaBox on the page TREE and not on each page, and
/// then PDFium reports neither box for the page itself. Ayaan's region render stopped there, so every tile of
/// those books (4 of 30 sampled from the user's library) came back empty. FPDF_GetPageBoundingBox follows the
/// inheritance; and if even that fails, the page's own size from the origin is what such a file almost always means.
fn page_box(page: &pdfium_render::prelude::PdfPage) -> pdfium_render::prelude::PdfRect {
    use pdfium_render::prelude::*;
    let boxes = page.boundaries();
    boxes
        .crop()
        .or_else(|_| boxes.media())
        .or_else(|_| boxes.bounding())
        .map(|b| b.bounds)
        .unwrap_or_else(|_| {
            PdfRect::new(PdfPoints::new(0.0), PdfPoints::new(0.0), page.height(), page.width())
        })
}

/// Every link annotation on a page, found by WALKING THE ANNOTATIONS.
///
/// ⚠️ NOT `page.links()`: that indexes the /Annots array rather than a list of links, and on a page
/// holding other annotations it reports links twice. And reading takes TWO questions: `/A <</S /URI>>`
/// answers `action()`, `/Dest` answers `destination()`. Chromium writes both forms in one file.
fn page_links(page: &pdfium_render::prelude::PdfPage) -> Vec<LinkInfo> {
    use pdfium_render::prelude::*;

    let mut out = Vec::new();
    let annotations = page.annotations();

    for i in 0..annotations.len() {
        let Ok(annotation) = annotations.get(i) else { continue };
        let PdfPageAnnotation::Link(ref link) = annotation else { continue };
        let Ok(bounds) = annotation.bounds() else { continue };
        let Ok(inner) = link.link() else { continue };

        let uri = inner
            .action()
            .and_then(|a| a.as_uri_action().and_then(|u| u.uri().ok()))
            .unwrap_or_default();

        let target_page = inner.destination().and_then(|d| d.page_index().ok()).map(|p| p as i32).unwrap_or(-1);

        let kind = if !uri.is_empty() {
            LINK_URI
        } else if target_page >= 0 {
            LINK_INTERNAL
        } else {
            LINK_OTHER
        };

        out.push(LinkInfo {
            index: i as usize,
            kind,
            uri,
            target_page,
            left: bounds.left().value,
            top: bounds.top().value,
            right: bounds.right().value,
            bottom: bounds.bottom().value,
        });
    }

    out
}

/// Every link on a page. Little-endian: a count, then per link the annotation index, the kind
/// (`LINK_*`), the box top-left origin with BOTH axes divided by the page WIDTH, the target page or -1,
/// and a length-prefixed UTF-8 address (empty for anything but a web link).
#[unsafe(no_mangle)]
pub extern "C" fn get_page_links(doc_handle: u64, page_index: i32) -> ByteBuffer {
    if doc_handle == 0 || page_index < 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_page_links_inner(doc_handle, page_index)).unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_page_links_inner(doc_handle: u64, page_index: i32) -> ByteBuffer {
    let _guard = call_guard();
    let Some(doc) = document(doc_handle) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);
    let Ok(page) = doc_guard.pages().get(page_index as u16) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let page_w = page.width().value;
    if page_w <= 0.0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    let (page_left, page_top) = page_origin(&page);
    let links = page_links(&page);

    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(&(links.len() as u32).to_le_bytes());
    for link in &links {
        out.extend_from_slice(&(link.index as u32).to_le_bytes());
        out.extend_from_slice(&link.kind.to_le_bytes());
        // PDF is bottom-left origin and Y-up; the app is top-left and Y-down.
        out.extend_from_slice(&((link.left - page_left) / page_w).to_le_bytes());
        out.extend_from_slice(&((page_top - link.top) / page_w).to_le_bytes());
        out.extend_from_slice(&((link.right - page_left) / page_w).to_le_bytes());
        out.extend_from_slice(&((page_top - link.bottom) / page_w).to_le_bytes());
        out.extend_from_slice(&link.target_page.to_le_bytes());
        out.extend_from_slice(&(link.uri.len() as u32).to_le_bytes());
        out.extend_from_slice(link.uri.as_bytes());
    }

    drop(page);
    drop(doc_guard);
    drop(doc);
    ByteBuffer::ok(out)
}

#[cfg(test)]
mod tests;
