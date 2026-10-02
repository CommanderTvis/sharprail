// The embedding API does not expose the negotiated keyboard protocol.
export fn gav_ghostty_agent_newline(surface: *Surface) bool {
    const core = &surface.core_surface;
    core.renderer_state.mutex.lock();
    defer core.renderer_state.mutex.unlock();
    const term = &core.io.terminal;
    if (term.flags.modify_other_keys_2 or term.screen.kitty_keyboard.current().int() != 0) return false;
    var data: @import("../termio.zig").Message.WriteReq.Small.Array = undefined;
    data[0] = 0x1b;
    data[1] = 0x0d;
    core.io.queueMessage(.{ .write_small = .{ .data = data, .len = 2 } }, .locked);
    return true;
}
