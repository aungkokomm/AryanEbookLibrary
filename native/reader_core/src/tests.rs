//! The reading core's own tests, on the fixtures copied from Ayaan PDF plus one of its own
//! (red_over_blue.pdf, for the byte order). Run with `cargo test --release` in native\reader_core.

use super::*;
use std::ffi::CString;

fn open(path: &str, password: Option<&str>) -> OpenResult {
    let c_path = CString::new(path).unwrap();
    let c_password = password.map(|p| CString::new(p).unwrap());
    open_document_protected(c_path.as_ptr(), c_password.as_ref().map_or(std::ptr::null(), |p| p.as_ptr()))
}

fn open_fixture(name: &str) -> u64 {
    let result = open(&format!("tests/fixtures/{name}"), None);
    assert_eq!(result.status, STATUS_OK_PDFIUM, "expected {name} to open");
    assert_ne!(result.handle, 0);
    result.handle
}

fn text_of(handle: u64, page: i32, width: i32) -> String {
    let array = get_page_chars(handle, page, width);
    assert_eq!(array.status, STATUS_OK_PDFIUM);
    let text = if array.chars.is_null() {
        String::new()
    } else {
        unsafe { std::slice::from_raw_parts(array.chars, array.len) }
            .iter()
            .filter_map(|c| char::from_u32(c.codepoint))
            .collect()
    };
    free_char_info_array(array);
    text
}

/// The BGRA bytes of one pixel of a render.
fn pixel(result: &RenderResult, x: i32, y: i32) -> [u8; 4] {
    let bytes = unsafe { std::slice::from_raw_parts(result.buffer, result.len) };
    let i = ((y * result.width + x) * 4) as usize;
    [bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]]
}

#[test]
fn a_missing_file_does_not_open() {
    let result = open("tests/fixtures/does-not-exist.pdf", None);
    assert_eq!(result.handle, 0);
    assert_eq!(result.status, STATUS_INVALID_INPUT);
}

#[test]
fn an_encrypted_file_asks_for_its_password_and_opens_with_the_right_one() {
    // The fixture Ayaan generates with the password "hunter2" (RC4 128-bit).
    let path = "tests/fixtures/sample_encrypted.pdf";
    assert_eq!(open(path, None).status, STATUS_NEEDS_PASSWORD);
    assert_eq!(open(path, Some("wrong")).status, STATUS_NEEDS_PASSWORD);

    let right = open(path, Some("hunter2"));
    assert_eq!(right.status, STATUS_OK_PDFIUM);
    assert_eq!(get_page_count(right.handle), 1);
    close_document(right.handle);
}

#[test]
fn every_page_has_a_size_and_it_matches_what_a_render_draws() {
    let handle = open_fixture("sample_20pages.pdf");
    assert_eq!(get_page_count(handle), 20);

    let array = get_page_sizes(handle);
    assert_eq!(array.status, STATUS_OK_PDFIUM);
    assert_eq!(array.len, 20);
    let sizes = unsafe { std::slice::from_raw_parts(array.sizes, array.len) }.to_vec();
    free_page_size_array(array);
    for (i, s) in sizes.iter().enumerate() {
        assert!(s.width > 0.0 && s.height > 0.0, "page {i} is {}x{}", s.width, s.height);
    }

    // A level-0 tile is the whole page 512 px wide, so its rows follow the page's aspect.
    let aspect = sizes[0].height / sizes[0].width;
    let rows = (aspect).ceil() as i32;
    for row in 0..rows {
        let tile = render_tile(handle, 0, 0, 0, row);
        assert_eq!(tile.status, STATUS_OK_PDFIUM, "row {row}");
        assert_eq!((tile.width, tile.height), (TILE_SIZE, TILE_SIZE), "a tile is square in pixels");
        free_render_result(tile);
    }
    assert_eq!(render_tile(handle, 0, 0, 0, rows).status, STATUS_INVALID_INPUT, "no row past the page");
    assert_eq!(render_tile(handle, 0, 0, 1, 0).status, STATUS_INVALID_INPUT, "level 0 is one tile across");

    close_document(handle);
}

#[test]
fn a_tile_is_bgra_so_red_and_blue_are_not_swapped() {
    // Red over blue: an asymmetric page, because a black-on-white one cannot tell BGRA from RGBA.
    let handle = open_fixture("red_over_blue.pdf");

    let tile = render_tile(handle, 0, 0, 0, 0);
    assert_eq!(tile.status, STATUS_OK_PDFIUM);
    assert_eq!(pixel(&tile, 256, 64), [0, 0, 255, 255], "the top half is red, which is B=0 G=0 R=255");
    assert_eq!(pixel(&tile, 256, 448), [255, 0, 0, 255], "the bottom half is blue, which is B=255 G=0 R=0");
    free_render_result(tile);

    // One level down the page is two tiles across; the bottom-right one is all blue, the top-left all red.
    let bottom_right = render_tile(handle, 0, 1, 1, 1);
    assert_eq!(pixel(&bottom_right, 10, 10), [255, 0, 0, 255]);
    assert_eq!(pixel(&bottom_right, 500, 500), [255, 0, 0, 255]);
    free_render_result(bottom_right);
    let top_left = render_tile(handle, 0, 1, 0, 0);
    assert_eq!(pixel(&top_left, 10, 10), [0, 0, 255, 255]);
    free_render_result(top_left);

    close_document(handle);
}

#[test]
fn a_whole_page_renders_at_the_asked_width_and_matches_its_tiles() {
    let handle = open_fixture("red_over_blue.pdf");

    let page = render_page_width(handle, 0, 1024);
    assert_eq!(page.status, STATUS_OK_PDFIUM);
    assert_eq!((page.width, page.height), (1024, 1024), "a square page stays square");
    assert_eq!(pixel(&page, 512, 100), [0, 0, 255, 255], "red on top");
    assert_eq!(pixel(&page, 512, 900), [255, 0, 0, 255], "blue below");

    // The same pixels as the level-1 tiles, which are the same page 1024 wide cut in four.
    let tile = render_tile(handle, 0, 1, 1, 1);
    assert_eq!(pixel(&page, 512 + 300, 512 + 300), pixel(&tile, 300, 300));
    free_render_result(tile);
    free_render_result(page);

    assert_eq!(render_page_width(handle, 0, 0).status, STATUS_INVALID_INPUT);
    assert_eq!(render_page_width(handle, 5, 100).status, STATUS_INVALID_INPUT);
    close_document(handle);

    let letter = open_fixture("sample_links.pdf");
    let page = render_page_width(letter, 0, 612);
    assert_eq!((page.width, page.height), (612, 792), "a US Letter page 612 wide is 792 tall");
    free_render_result(page);
    close_document(letter);
}

#[test]
fn a_cached_tile_comes_back_the_same_and_closing_forgets_it() {
    let handle = open_fixture("red_over_blue.pdf");

    let first = render_tile(handle, 0, 2, 1, 1);
    let second = render_tile(handle, 0, 2, 1, 1);
    let a = unsafe { std::slice::from_raw_parts(first.buffer, first.len) }.to_vec();
    let b = unsafe { std::slice::from_raw_parts(second.buffer, second.len) }.to_vec();
    assert_eq!(a, b);
    free_render_result(first);
    free_render_result(second);

    let key = TileKey { doc: handle, page: 0, level: 2, col: 1, row: 1 };
    assert!(lock(&core().cache).peek(&key).is_some(), "the tile was cached");
    close_document(handle);
    assert!(lock(&core().cache).peek(&key).is_none(), "closing the book drops its tiles");
    assert_eq!(render_tile(handle, 0, 0, 0, 0).status, STATUS_INVALID_INPUT, "a closed handle renders nothing");
}

#[test]
fn a_page_reads_back_its_text_in_order_inside_the_asked_width() {
    let handle = open_fixture("sample_20pages.pdf");
    assert_eq!(text_of(handle, 0, 200), "Page 1 of 20");
    assert_eq!(text_of(handle, 5, 200), "Page 6 of 20");

    let array = get_page_chars(handle, 0, 200);
    let chars = unsafe { std::slice::from_raw_parts(array.chars, array.len) };
    for pair in chars.windows(2) {
        assert!(pair[1].left >= pair[0].left - 1.0, "left to right");
    }
    for c in chars {
        assert!(c.left >= 0.0 && c.right <= 201.0, "box {}..{} outside the width", c.left, c.right);
        assert!(c.top >= 0.0 && c.bottom > c.top, "top-left origin with a height");
    }
    free_char_info_array(array);

    assert_eq!(get_page_chars(0, 0, 200).status, STATUS_INVALID_INPUT);
    assert_eq!(get_page_chars(handle, 99, 200).status, STATUS_INVALID_INPUT);
    close_document(handle);
}

#[test]
fn a_scanned_page_has_no_text_and_that_is_not_a_failure() {
    let handle = open_fixture("sample_scanned.pdf");
    assert_eq!(text_of(handle, 0, 400), "");
    close_document(handle);
}

#[derive(Debug, PartialEq)]
struct Mark {
    depth: i32,
    page: i32,
    title: String,
}

fn marks_of(handle: u64) -> Vec<Mark> {
    let buf = get_bookmarks(handle);
    assert_eq!(buf.status, STATUS_OK_PDFIUM);
    let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) }.to_vec();
    free_byte_buffer(buf);

    let count = u32::from_le_bytes(bytes[0..4].try_into().unwrap()) as usize;
    let mut out = Vec::new();
    let mut p = 4;
    for _ in 0..count {
        let depth = i32::from_le_bytes(bytes[p..p + 4].try_into().unwrap());
        let page = i32::from_le_bytes(bytes[p + 4..p + 8].try_into().unwrap());
        let len = u32::from_le_bytes(bytes[p + 8..p + 12].try_into().unwrap()) as usize;
        let title = String::from_utf8(bytes[p + 12..p + 12 + len].to_vec()).unwrap();
        p += 12 + len;
        out.push(Mark { depth, page, title });
    }
    assert_eq!(p, bytes.len(), "the entries fill the buffer exactly");
    out
}

#[test]
fn the_outline_comes_in_reading_order_with_its_nesting() {
    let handle = open_fixture("sample_outline.pdf");
    let shape: Vec<(String, i32, i32)> = marks_of(handle).into_iter().map(|m| (m.title, m.depth, m.page)).collect();
    assert_eq!(
        shape,
        vec![
            ("Chapter One".to_string(), 0, 0),
            ("Section 1.1".to_string(), 1, 1),
            // A GoTo ACTION rather than a /Dest: real files use both.
            ("Chapter Two".to_string(), 0, 2),
            // Goes nowhere, and is still listed.
            ("Nowhere".to_string(), 0, -1),
        ]
    );
    close_document(handle);

    let plain = open_fixture("sample_20pages.pdf");
    assert!(marks_of(plain).is_empty(), "no outline is an empty answer, not a failure");
    close_document(plain);
    assert_eq!(get_bookmarks(0).status, STATUS_INVALID_INPUT);
}

#[derive(Debug)]
struct Link {
    kind: u32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    target: i32,
    uri: String,
}

fn links_of(handle: u64, page: i32) -> Vec<Link> {
    let buf = get_page_links(handle, page);
    assert_eq!(buf.status, STATUS_OK_PDFIUM);
    let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) }.to_vec();
    free_byte_buffer(buf);

    let u32_at = |p: usize| u32::from_le_bytes(bytes[p..p + 4].try_into().unwrap());
    let f32_at = |p: usize| f32::from_le_bytes(bytes[p..p + 4].try_into().unwrap());
    let count = u32_at(0) as usize;
    let mut out = Vec::new();
    let mut p = 4;
    for _ in 0..count {
        let kind = u32_at(p + 4);
        let (left, top, right, bottom) = (f32_at(p + 8), f32_at(p + 12), f32_at(p + 16), f32_at(p + 20));
        let target = u32_at(p + 24) as i32;
        let len = u32_at(p + 28) as usize;
        let uri = String::from_utf8(bytes[p + 32..p + 32 + len].to_vec()).unwrap();
        p += 32 + len;
        out.push(Link { kind, left, top, right, bottom, target, uri });
    }
    assert_eq!(p, bytes.len());
    out
}

#[test]
fn links_read_back_with_their_addresses_targets_and_boxes() {
    // Edge's own output: two web links and one jump to page 2 on page 1, and a jump back on page 2.
    let handle = open_fixture("sample_links.pdf");
    let page0 = links_of(handle, 0);
    assert_eq!(page0.len(), 3, "{page0:#?}");
    assert_eq!((page0[0].kind, page0[0].uri.as_str()), (LINK_URI, "https://example.com/path?q=1"));
    assert_eq!((page0[1].kind, page0[1].uri.as_str()), (LINK_URI, "mailto:someone@example.com"));
    assert_eq!((page0[2].kind, page0[2].target), (LINK_INTERNAL, 1));

    // The first link sits at PDF x 190.5..273.75, y 640.5..656.25 on a 612x792 page, and the app's
    // space is top-left origin with both axes over the page WIDTH.
    let first = &page0[0];
    assert!((first.left - 190.5 / 612.0).abs() < 1e-4, "{first:?}");
    assert!((first.right - 273.75 / 612.0).abs() < 1e-4, "{first:?}");
    assert!((first.top - (792.0 - 656.25) / 612.0).abs() < 1e-4, "{first:?}");
    assert!((first.bottom - (792.0 - 640.5) / 612.0).abs() < 1e-4, "{first:?}");

    let page1 = links_of(handle, 1);
    assert_eq!((page1[0].kind, page1[0].target), (LINK_INTERNAL, 0));
    close_document(handle);

    let plain = open_fixture("sample_20pages.pdf");
    assert!(links_of(plain, 3).is_empty());
    close_document(plain);
}

#[test]
fn a_page_whose_box_is_inherited_from_the_page_tree_still_renders_tiles() {
    // Many real books put /MediaBox on the page tree, not on each page, and PDFium then reports no box for the
    // page itself. The tile renderer read the page's own box and gave up: every tile of those books was blank.
    let handle = open_fixture("inherited_box.pdf");
    let tile = render_tile(handle, 0, 1, 1, 1);
    assert_eq!(tile.status, STATUS_OK_PDFIUM, "a tile of a page with an inherited box");
    assert_eq!((tile.width, tile.height), (TILE_SIZE, TILE_SIZE));
    assert_eq!(pixel(&tile, 300, 300), [255, 0, 0, 255], "the bottom-right quarter is blue");
    free_render_result(tile);
    let top_left = render_tile(handle, 0, 1, 0, 0);
    assert_eq!(pixel(&top_left, 300, 300), [0, 0, 255, 255], "the top-left quarter is red");
    free_render_result(top_left);

    // The page still measures the same after a tile narrowed and restored its box.
    let array = get_page_sizes(handle);
    let size = unsafe { *array.sizes };
    free_page_size_array(array);
    assert_eq!((size.width, size.height), (200.0, 200.0));
    close_document(handle);
}
