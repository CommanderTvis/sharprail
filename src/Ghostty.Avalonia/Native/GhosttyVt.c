// A small, blittable C ABI over libghostty-vt for the Skia renderer. libghostty-vt's own API uses
// sized structs and opaque iterators that change between unreleased revisions; this file is the only
// code that tracks them. Every function runs on the caller's UI thread.
#include <stdlib.h>
#include <string.h>
#include <ghostty/vt.h>

#if defined(_WIN32)
#define GAV_API __declspec(dllexport)
#else
#define GAV_API __attribute__((visibility("default")))
#endif

typedef void (*gav_vt_write_cb)(void *context, const uint8_t *data, size_t len);
void gav_vt_install_clipboard(GhosttyTerminal terminal);

enum {
    GAV_CELL_BOLD = 1 << 0, GAV_CELL_ITALIC = 1 << 1, GAV_CELL_FAINT = 1 << 2, GAV_CELL_BLINK = 1 << 3,
    GAV_CELL_INVERSE = 1 << 4, GAV_CELL_INVISIBLE = 1 << 5, GAV_CELL_STRIKETHROUGH = 1 << 6,
    GAV_CELL_OVERLINE = 1 << 7, GAV_CELL_SELECTED = 1 << 8,
};

// Colors are 0xAARRGGBB; a zero alpha means the terminal's default color.
typedef struct {
    uint32_t text;      // offset of the cell's code points in the frame's text buffer
    uint16_t length;    // code point count; 0 for an empty cell
    uint8_t wide;       // GhosttyCellWide
    uint8_t underline;  // GHOSTTY_SGR_UNDERLINE_*
    uint32_t foreground;
    uint32_t background;
    uint32_t underline_color;
    uint16_t flags;
    uint16_t row_wrapped; // only populated on the first cell of each row
} gav_vt_cell;

typedef struct {
    uint16_t columns, rows;
    uint32_t background, foreground, cursor_color;
    uint16_t cursor_x, cursor_y;
    uint8_t cursor_visible, cursor_blinking, cursor_style, cursor_wide_tail;
    uint8_t dirty, alternate_screen, mouse_tracking, reserved;
    uint64_t scroll_total, scroll_offset, scroll_length;
    const gav_vt_cell *cells;
    const uint32_t *text;
} gav_vt_frame;

typedef struct {
    GhosttyTerminal terminal;
    GhosttyRenderState render;
    GhosttyRenderStateRowIterator rows;
    GhosttyRenderStateRowCells row_cells;
    GhosttyKeyEncoder keys;
    GhosttyKeyEvent key;
    GhosttyMouseEncoder mouse;
    GhosttyMouseEvent mouse_event;
    GhosttySelectionGesture gesture;
    GhosttySelectionGestureEvent gesture_events[3];
    gav_vt_write_cb write;
    void *context;
    uint32_t cell_width, cell_height;
    gav_vt_cell *cells;
    size_t cell_capacity;
    uint32_t *text;
    size_t text_capacity;
    bool option_as_alt;
} gav_vt;

static uint32_t argb(GhosttyColorRgb color) { return 0xFF000000u | (uint32_t)color.r << 16 | (uint32_t)color.g << 8 | color.b; }
static GhosttyColorRgb rgb(uint32_t value) { return (GhosttyColorRgb){ (uint8_t)(value >> 16), (uint8_t)(value >> 8), (uint8_t)value }; }
static uint32_t resolve(GhosttyStyleColor color, const GhosttyRenderStateColors *colors) {
    switch (color.tag) {
        case GHOSTTY_STYLE_COLOR_RGB: return argb(color.value.rgb);
        case GHOSTTY_STYLE_COLOR_PALETTE: return argb(colors->palette[color.value.palette]);
        default: return 0;
    }
}

static void write_pty(GhosttyTerminal terminal, void *userdata, const uint8_t *data, size_t len) {
    (void)terminal;
    gav_vt *vt = userdata;
    if (vt->write && len) vt->write(vt->context, data, len);
}

GAV_API void gav_vt_free(gav_vt *vt) {
    if (!vt) return;
    for (int i = 0; i < 3; i++) if (vt->gesture_events[i]) ghostty_selection_gesture_event_free(vt->gesture_events[i]);
    if (vt->gesture) ghostty_selection_gesture_free(vt->gesture, vt->terminal);
    if (vt->mouse_event) ghostty_mouse_event_free(vt->mouse_event);
    if (vt->mouse) ghostty_mouse_encoder_free(vt->mouse);
    if (vt->key) ghostty_key_event_free(vt->key);
    if (vt->keys) ghostty_key_encoder_free(vt->keys);
    if (vt->row_cells) ghostty_render_state_row_cells_free(vt->row_cells);
    if (vt->rows) ghostty_render_state_row_iterator_free(vt->rows);
    if (vt->render) ghostty_render_state_free(vt->render);
    if (vt->terminal) ghostty_terminal_free(vt->terminal);
    free(vt->cells);
    free(vt->text);
    free(vt);
}

GAV_API gav_vt *gav_vt_new(uint16_t columns, uint16_t rows, size_t scrollback_lines, gav_vt_write_cb write, void *context) {
    gav_vt *vt = calloc(1, sizeof *vt);
    if (!vt) return NULL;
    vt->write = write;
    vt->context = context;
    vt->cell_width = vt->cell_height = 1;
    static const GhosttySelectionGestureEventType types[3] = {
        GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_PRESS, GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_DRAG, GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_RELEASE };
    bool ok = ghostty_terminal_new(NULL, &vt->terminal, columns, rows) == GHOSTTY_SUCCESS &&
        ghostty_render_state_new(NULL, &vt->render) == GHOSTTY_SUCCESS &&
        ghostty_render_state_row_iterator_new(NULL, &vt->rows) == GHOSTTY_SUCCESS &&
        ghostty_render_state_row_cells_new(NULL, &vt->row_cells) == GHOSTTY_SUCCESS &&
        ghostty_key_encoder_new(NULL, &vt->keys) == GHOSTTY_SUCCESS &&
        ghostty_key_event_new(NULL, &vt->key) == GHOSTTY_SUCCESS &&
        ghostty_mouse_encoder_new(NULL, &vt->mouse) == GHOSTTY_SUCCESS &&
        ghostty_mouse_event_new(NULL, &vt->mouse_event) == GHOSTTY_SUCCESS &&
        ghostty_selection_gesture_new(NULL, &vt->gesture) == GHOSTTY_SUCCESS;
    for (int i = 0; ok && i < 3; i++) ok = ghostty_selection_gesture_event_new(NULL, &vt->gesture_events[i], types[i]) == GHOSTTY_SUCCESS;
    if (!ok) { gav_vt_free(vt); return NULL; }
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_USERDATA, vt);
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_WRITE_PTY, (const void *)write_pty);
    gav_vt_install_clipboard(vt->terminal);
    GhosttyTerminalModeConfig graphemes = { .mode = GHOSTTY_MODE_GRAPHEME_CLUSTER, .value = true };
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_MODE_DEFAULT, &graphemes);
    size_t lines = scrollback_lines;
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_LINES, &lines);
    return vt;
}

GAV_API void gav_vt_write(gav_vt *vt, const uint8_t *data, size_t len) { ghostty_terminal_vt_write(vt->terminal, data, len); }

GAV_API void gav_vt_resize(gav_vt *vt, uint16_t columns, uint16_t rows, uint32_t cell_width, uint32_t cell_height) {
    vt->cell_width = cell_width ? cell_width : 1;
    vt->cell_height = cell_height ? cell_height : 1;
    ghostty_terminal_resize(vt->terminal, columns, rows, vt->cell_width, vt->cell_height);
    GhosttyMouseEncoderSize size = GHOSTTY_INIT_SIZED(GhosttyMouseEncoderSize);
    size.screen_width = columns * vt->cell_width;
    size.screen_height = rows * vt->cell_height;
    size.cell_width = vt->cell_width;
    size.cell_height = vt->cell_height;
    ghostty_mouse_encoder_setopt(vt->mouse, GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &size);
}

// colors: background, foreground, cursor (zero alpha: unset), then the 16 ANSI colors.
GAV_API void gav_vt_set_colors(gav_vt *vt, const uint32_t *colors) {
    GhosttyColorRgb background = rgb(colors[0]), foreground = rgb(colors[1]), cursor = rgb(colors[2]);
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &background);
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &foreground);
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_CURSOR, colors[2] >> 24 ? &cursor : NULL);
    GhosttyColorRgb palette[256];
    ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLOR_PALETTE_DEFAULT, palette);
    for (int i = 0; i < 16; i++) palette[i] = rgb(colors[3 + i]);
    ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, palette);
}

GAV_API void gav_vt_set_option_as_alt(gav_vt *vt, bool value) { vt->option_as_alt = value; }

static bool mode(gav_vt *vt, GhosttyMode which) {
    GhosttyTerminalModeConfig config = { .mode = which };
    return ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_MODE, &config) == GHOSTTY_SUCCESS && config.value;
}

static bool reserve(void **buffer, size_t *capacity, size_t count, size_t size) {
    if (count <= *capacity) return true;
    size_t next = *capacity ? *capacity : 256;
    while (next < count) next *= 2;
    void *grown = realloc(*buffer, next * size);
    if (!grown) return false;
    *buffer = grown;
    *capacity = next;
    return true;
}

// Captures the viewport. The returned buffers stay valid until the next snapshot or gav_vt_free.
GAV_API bool gav_vt_snapshot(gav_vt *vt, gav_vt_frame *frame) {
    memset(frame, 0, sizeof *frame);
    if (ghostty_render_state_update(vt->render, vt->terminal) != GHOSTTY_SUCCESS) return false;
    GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
    ghostty_render_state_get(vt->render, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);
    ghostty_render_state_get(vt->render, GHOSTTY_RENDER_STATE_DATA_COLS, &frame->columns);
    ghostty_render_state_get(vt->render, GHOSTTY_RENDER_STATE_DATA_ROWS, &frame->rows);
    GhosttyRenderStateColors colors = GHOSTTY_INIT_SIZED(GhosttyRenderStateColors);
    ghostty_render_state_get(vt->render, GHOSTTY_RENDER_STATE_DATA_COLORS, &colors);
    frame->background = argb(colors.background);
    frame->foreground = argb(colors.foreground);
    frame->cursor_color = colors.cursor_has_value ? argb(colors.cursor) : 0;
    GhosttyRenderStateCursor cursor = GHOSTTY_INIT_SIZED(GhosttyRenderStateCursor);
    ghostty_render_state_get(vt->render, GHOSTTY_RENDER_STATE_DATA_CURSOR, &cursor);
    frame->cursor_visible = cursor.visible && cursor.viewport_has_value;
    if (frame->cursor_visible) {
        frame->cursor_x = cursor.viewport_x;
        frame->cursor_y = cursor.viewport_y;
        frame->cursor_wide_tail = cursor.wide_tail;
    }
    frame->cursor_blinking = cursor.blinking;
    frame->cursor_style = (uint8_t)cursor.visual_style;
    frame->dirty = (uint8_t)dirty;
    GhosttyTerminalScreen screen = GHOSTTY_TERMINAL_SCREEN_PRIMARY;
    ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &screen);
    frame->alternate_screen = screen == GHOSTTY_TERMINAL_SCREEN_ALTERNATE;
    bool tracking = false;
    ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING, &tracking);
    frame->mouse_tracking = tracking;
    GhosttyTerminalScrollbar scrollbar = { 0 };
    if (ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_SCROLLBAR, &scrollbar) == GHOSTTY_SUCCESS) {
        frame->scroll_total = scrollbar.total;
        frame->scroll_offset = scrollbar.offset;
        frame->scroll_length = scrollbar.len;
    }

    size_t count = (size_t)frame->columns * frame->rows;
    if (!reserve((void **)&vt->cells, &vt->cell_capacity, count ? count : 1, sizeof *vt->cells)) return false;
    memset(vt->cells, 0, count * sizeof *vt->cells);
    size_t used = 0;
    if (ghostty_render_state_get(vt->render, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &vt->rows) != GHOSTTY_SUCCESS) return false;
    for (uint16_t y = 0; y < frame->rows && ghostty_render_state_row_iterator_next(vt->rows); y++) {
        GhosttyRow raw_row = 0;
        bool wrapped = false;
        ghostty_render_state_row_get(vt->rows, GHOSTTY_RENDER_STATE_ROW_DATA_RAW, &raw_row);
        ghostty_row_get(raw_row, GHOSTTY_ROW_DATA_WRAP, &wrapped);
        vt->cells[(size_t)y * frame->columns].row_wrapped = wrapped ? 1 : 0;
        GhosttyRenderStateRowSelection selection = GHOSTTY_INIT_SIZED(GhosttyRenderStateRowSelection);
        bool selected = ghostty_render_state_row_get(vt->rows, GHOSTTY_RENDER_STATE_ROW_DATA_SELECTION, &selection) == GHOSTTY_SUCCESS;
        if (ghostty_render_state_row_get(vt->rows, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &vt->row_cells) != GHOSTTY_SUCCESS) continue;
        for (uint16_t x = 0; x < frame->columns && ghostty_render_state_row_cells_next(vt->row_cells); x++) {
            gav_vt_cell *cell = &vt->cells[(size_t)y * frame->columns + x];
            GhosttyCell raw = 0;
            ghostty_render_state_row_cells_get(vt->row_cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw);
            GhosttyCellWide wide = GHOSTTY_CELL_WIDE_NARROW;
            ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide);
            cell->wide = (uint8_t)wide;
            uint32_t length = 0;
            ghostty_render_state_row_cells_get(vt->row_cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_LEN, &length);
            if (length) {
                if (!reserve((void **)&vt->text, &vt->text_capacity, used + length, sizeof *vt->text)) return false;
                ghostty_render_state_row_cells_get(vt->row_cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_BUF, vt->text + used);
                cell->text = (uint32_t)used;
                cell->length = (uint16_t)length;
                used += length;
            }
            GhosttyStyle style = GHOSTTY_INIT_SIZED(GhosttyStyle);
            ghostty_render_state_row_cells_get(vt->row_cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style);
            cell->foreground = resolve(style.fg_color, &colors);
            cell->underline_color = resolve(style.underline_color, &colors);
            // Background-only cells carry their color in the cell content rather than a style.
            GhosttyColorRgb background;
            cell->background = ghostty_render_state_row_cells_get(vt->row_cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &background) == GHOSTTY_SUCCESS
                ? argb(background) : 0;
            cell->underline = (uint8_t)style.underline;
            cell->flags = (style.bold ? GAV_CELL_BOLD : 0) | (style.italic ? GAV_CELL_ITALIC : 0) | (style.faint ? GAV_CELL_FAINT : 0) |
                (style.blink ? GAV_CELL_BLINK : 0) | (style.inverse ? GAV_CELL_INVERSE : 0) | (style.invisible ? GAV_CELL_INVISIBLE : 0) |
                (style.strikethrough ? GAV_CELL_STRIKETHROUGH : 0) | (style.overline ? GAV_CELL_OVERLINE : 0) |
                (selected && x >= selection.start_x && x <= selection.end_x ? GAV_CELL_SELECTED : 0);
        }
        bool clean = false;
        ghostty_render_state_row_set(vt->rows, GHOSTTY_RENDER_STATE_ROW_OPTION_DIRTY, &clean);
    }
    frame->cells = vt->cells;
    frame->text = vt->text;
    GhosttyRenderStateDirty clean = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
    ghostty_render_state_set(vt->render, GHOSTTY_RENDER_STATE_OPTION_DIRTY, &clean);
    return true;
}

// Each encoder writes at most capacity bytes and returns the encoded length, 0 when nothing is sent.
GAV_API size_t gav_vt_key(gav_vt *vt, int32_t action, int32_t key, uint16_t mods, uint16_t consumed_mods,
                          const char *text, uint32_t unshifted, char *out, size_t capacity) {
    ghostty_key_encoder_setopt_from_terminal(vt->keys, vt->terminal);
    GhosttyOptionAsAlt alt = vt->option_as_alt ? GHOSTTY_OPTION_AS_ALT_TRUE : GHOSTTY_OPTION_AS_ALT_FALSE;
    ghostty_key_encoder_setopt(vt->keys, GHOSTTY_KEY_ENCODER_OPT_MACOS_OPTION_AS_ALT, &alt);
    ghostty_key_event_set_action(vt->key, (GhosttyKeyAction)action);
    ghostty_key_event_set_key(vt->key, (GhosttyKey)key);
    ghostty_key_event_set_mods(vt->key, mods);
    ghostty_key_event_set_consumed_mods(vt->key, consumed_mods);
    ghostty_key_event_set_utf8(vt->key, text, text ? strlen(text) : 0);
    ghostty_key_event_set_unshifted_codepoint(vt->key, unshifted);
    size_t written = 0;
    return ghostty_key_encoder_encode(vt->keys, vt->key, out, capacity, &written) == GHOSTTY_SUCCESS ? written : 0;
}

// Returns false when the terminal does not track the mouse, so the caller handles the gesture itself.
GAV_API bool gav_vt_mouse(gav_vt *vt, int32_t action, int32_t button, uint16_t mods, float x, float y,
                          char *out, size_t capacity, size_t *written) {
    *written = 0;
    bool tracking = false;
    ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING, &tracking);
    if (!tracking) return false;
    ghostty_mouse_encoder_setopt_from_terminal(vt->mouse, vt->terminal);
    ghostty_mouse_event_set_action(vt->mouse_event, (GhosttyMouseAction)action);
    if (button) ghostty_mouse_event_set_button(vt->mouse_event, (GhosttyMouseButton)button);
    else ghostty_mouse_event_clear_button(vt->mouse_event);
    ghostty_mouse_event_set_mods(vt->mouse_event, mods);
    ghostty_mouse_event_set_position(vt->mouse_event, (GhosttyMousePosition){ .x = x, .y = y });
    if (ghostty_mouse_encoder_encode(vt->mouse, vt->mouse_event, out, capacity, written) != GHOSTTY_SUCCESS) *written = 0;
    return true;
}

GAV_API size_t gav_vt_paste(gav_vt *vt, const char *text, size_t len, char *out, size_t capacity) {
    char *copy = malloc(len ? len : 1);
    if (!copy) return 0;
    memcpy(copy, text, len);
    size_t written = 0;
    GhosttyResult result = ghostty_paste_encode(copy, len, mode(vt, GHOSTTY_MODE_BRACKETED_PASTE), out, capacity, &written);
    free(copy);
    return result == GHOSTTY_SUCCESS ? written : 0;
}

GAV_API size_t gav_vt_focus(gav_vt *vt, bool gained, char *out, size_t capacity) {
    if (!mode(vt, GHOSTTY_MODE_FOCUS_EVENT)) return 0;
    size_t written = 0;
    return ghostty_focus_encode(gained ? GHOSTTY_FOCUS_GAINED : GHOSTTY_FOCUS_LOST, out, capacity, &written) == GHOSTTY_SUCCESS ? written : 0;
}

// delta rows: negative scrolls back into history; zero returns to the bottom.
GAV_API void gav_vt_scroll(gav_vt *vt, int64_t delta) {
    GhosttyTerminalScrollViewport scroll = { .tag = delta ? GHOSTTY_SCROLL_VIEWPORT_DELTA : GHOSTTY_SCROLL_VIEWPORT_BOTTOM };
    scroll.value.delta = (intptr_t)delta;
    ghostty_terminal_scroll_viewport(vt->terminal, scroll);
}

// kind: 0 press, 1 drag, 2 release, at a pixel position inside the grid.
GAV_API void gav_vt_select(gav_vt *vt, int32_t kind, double x, double y, uint64_t time_ns) {
    uint16_t columns = 0, rows = 0;
    ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLS, &columns);
    ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ROWS, &rows);
    if (!columns || !rows) return;
    double column = x / vt->cell_width, row = y / vt->cell_height;
    GhosttyPoint point = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = {
        .x = (uint16_t)(column < 0 ? 0 : column >= columns ? columns - 1 : column),
        .y = (uint32_t)(row < 0 ? 0 : row >= rows ? rows - 1 : row) } } };
    GhosttyGridRef ref = GHOSTTY_INIT_SIZED(GhosttyGridRef);
    if (ghostty_terminal_grid_ref(vt->terminal, point, &ref) != GHOSTTY_SUCCESS) return;
    GhosttySelectionGestureEvent event = vt->gesture_events[kind];
    GhosttySurfacePosition position = { .x = x, .y = y };
    GhosttySelectionGestureGeometry geometry = { .columns = columns, .cell_width = vt->cell_width, .screen_height = rows * vt->cell_height };
    ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &ref);
    ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &position);
    ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_GEOMETRY, &geometry);
    ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_TIME_NS, &time_ns);
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    GhosttyResult result = ghostty_selection_gesture_event(vt->gesture, vt->terminal, event, &selection);
    if (result == GHOSTTY_SUCCESS) ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
    else if (kind == 0) ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
}

GAV_API void gav_vt_select_all(gav_vt *vt) {
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    if (ghostty_terminal_select_all(vt->terminal, &selection) == GHOSTTY_SUCCESS)
        ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
}

GAV_API void gav_vt_clear_selection(gav_vt *vt) { ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL); }

static size_t format(gav_vt *vt, const GhosttySelection *selection, char *out, size_t capacity) {
    GhosttyTerminalSelectionFormatOptions options = GHOSTTY_INIT_SIZED(GhosttyTerminalSelectionFormatOptions);
    options.emit = GHOSTTY_FORMATTER_FORMAT_PLAIN;
    options.unwrap = true;
    options.trim = true;
    options.selection = selection;
    size_t written = 0;
    GhosttyResult result = ghostty_terminal_selection_format_buf(vt->terminal, options, (uint8_t *)out, capacity, &written);
    return result == GHOSTTY_SUCCESS || result == GHOSTTY_OUT_OF_SPACE ? written : 0;
}

// Returns the length of the selected text, which may exceed capacity; 0 without a selection.
GAV_API size_t gav_vt_selection_text(gav_vt *vt, char *out, size_t capacity) {
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    if (ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_SELECTION, &selection) != GHOSTTY_SUCCESS) return 0;
    return format(vt, &selection, out, capacity);
}

// The whole screen and scrollback as plain text, for accessibility and checks.
GAV_API size_t gav_vt_screen_text(gav_vt *vt, char *out, size_t capacity) {
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    if (ghostty_terminal_select_all(vt->terminal, &selection) != GHOSTTY_SUCCESS) return 0;
    return format(vt, &selection, out, capacity);
}
