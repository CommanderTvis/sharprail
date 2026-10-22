
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
