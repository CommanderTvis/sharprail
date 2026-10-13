
// Read a link without sending mouse input or changing the hover state.
pub fn contextLink(self: *Surface, pos: apprt.CursorPos, shift: bool, output: []u8) isize {
    self.renderer_state.mutex.lock();
    defer self.renderer_state.mutex.unlock();
    if (self.io.terminal.flags.mouse_event != .none and !shift) return -1;
    const mods = self.mouse.mods;
    self.mouse.mods = input.ctrlOrSuper(.{});
    defer self.mouse.mods = mods;
    const found = (self.linkAtPos(pos) catch return 0) orelse return 0;
    const uri = switch (found[0]) {
        .open => self.io.terminal.screen.selectionString(self.alloc, .{ .sel = found[1], .trim = false }) catch return 0,
        ._open_osc8 => self.alloc.dupe(u8, self.osc8URI(found[1].start()) orelse return 0) catch return 0,
    };
    defer self.alloc.free(uri);
    if (uri.len > output.len) return 0;
    @memcpy(output[0..uri.len], uri);
    return @intCast(uri.len);
}
