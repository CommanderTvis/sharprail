
// Prompt features over the OSC 133 marks the shell integration writes (appended to Surface.zig).

// The text of the command output at a point, as Ghostty's Cmd/Ctrl+triple-click selects it (Screen.selectOutput), copied
// into output. Returns its length, which a caller with too small a buffer allocates again for, or -1 when the point is not
// in a command's output.
// With `select`, the output also becomes the selection.
pub fn commandOutputText(self: *Surface, pos: apprt.CursorPos, select: bool, output: []u8) isize {
    self.renderer_state.mutex.lock();
    defer self.renderer_state.mutex.unlock();
    const t = &self.io.terminal;
    if (t.active_screen != .primary) return -1;
    const point = self.posToViewport(pos.x, pos.y);
    const pin = t.screen.pages.pin(.{ .viewport = point }) orelse return -1;
    const sel = t.screen.selectOutput(pin) orelse return -1;
    if (select) {
        t.screen.select(sel) catch return -1;
        self.queueRender() catch {};
    }
    const text = t.screen.selectionString(self.alloc, .{ .sel = sel, .trim = true }) catch return -1;
    defer self.alloc.free(text);
    if (text.len <= output.len) @memcpy(output[0..text.len], text);
    return @intCast(text.len);
}

// Cursor-click-to-move for a plain click, as Ghostty's current cursor-click-to-move does it (Ghostty 1.2.3 itself wants
// Option held): while the shell waits for input (OSC 133 B written and C not yet), a click on the input line becomes the
// arrow keys that move the shell's cursor there. Ghostty 1.2.3 rows know only that they hold input, not which cells, so
// the line is the cursor's row and the rows soft-wrapped with it, and a click past the text stops one cell after it.
// Returns true when arrows were sent.
pub fn promptClickMove(self: *Surface, pos: apprt.CursorPos) bool {
    self.renderer_state.mutex.lock();
    defer self.renderer_state.mutex.unlock();
    const t = &self.io.terminal;
    if (t.active_screen != .primary or t.flags.mouse_event != .none or !t.flags.shell_redraws_prompt) return false;
    const screen = &t.screen;
    if (screen.selection != null) return false;
    const cols: usize = screen.pages.cols;
    const cursor = screen.cursor.page_pin.*;
    const click = screen.pages.pin(.{ .viewport = self.posToViewport(pos.x, pos.y) }) orelse return false;
    var first = cursor;
    while (first.up(1)) |above| {
        if (!above.rowAndCell().row.wrap) break;
        first = above;
    }
    if (first.rowAndCell().row.semantic_prompt != .input) return false;
    var last = cursor;
    while (last.rowAndCell().row.wrap) last = last.down(1) orelse break;
    const top: usize = screen.pages.pointFromPin(.screen, first).?.screen.y;
    const bottom: usize = screen.pages.pointFromPin(.screen, last).?.screen.y;
    const at = screen.pages.pointFromPin(.screen, click).?.screen;
    const from = screen.pages.pointFromPin(.screen, cursor).?.screen;
    if (at.y < top or at.y > bottom) return false;
    // One past the last written cell of the line, walking its rows from the first.
    var end: usize = 0;
    var row_pin: ?terminal.Pin = first;
    var offset: usize = 0;
    while (row_pin) |row| : (offset += cols) {
        const cells = row.node.data.getCells(row.rowAndCell().row);
        for (cells, 0..) |cell, x| {
            if (cell.hasText()) end = offset + x + 1;
        }
        if (offset / cols >= bottom - top) break;
        row_pin = row.down(1);
    }
    const target = @min((at.y - top) * cols + at.x, @max(end, (from.y - top) * cols + from.x));
    const current = (from.y - top) * cols + from.x;
    if (target == current) return false;
    const low = @min(target, current);
    const high = @max(target, current);
    var count: usize = 0;
    var index = low;
    while (index < high) : (index += 1) {
        const pin = screen.pages.pin(.{ .screen = .{ .x = @intCast(index % cols), .y = @intCast(top + index / cols) } }) orelse continue;
        switch (pin.rowAndCell().cell.wide) {
            .spacer_tail, .spacer_head => {},
            else => count += 1,
        }
    }
    if (count == 0) return false;
    const keys = t.modes.get(.cursor_keys);
    const arrow: []const u8 = if (target > current)
        (if (keys) "\x1bOC" else "\x1b[C")
    else
        (if (keys) "\x1bOD" else "\x1b[D");
    for (0..count) |_| self.io.queueMessage(.{ .write_stable = arrow }, .locked);
    return true;
}

// The viewport rows holding the command line of a command that exited non-zero (CommandEnd.patch), up to rows.len.
pub fn failedCommandRows(self: *Surface, rows: []u16) usize {
    self.renderer_state.mutex.lock();
    defer self.renderer_state.mutex.unlock();
    const t = &self.io.terminal;
    if (t.active_screen != .primary) return 0;
    var count: usize = 0;
    var y: usize = 0;
    while (y < t.rows and count < rows.len) : (y += 1) {
        const pin = t.screen.pages.pin(.{ .viewport = .{ .x = 0, .y = @intCast(y) } }) orelse break;
        if (pin.rowAndCell().row.command_failed) {
            rows[count] = @intCast(y);
            count += 1;
        }
    }
    return count;
}
