
// Prompt features over OSC 133 marks (appended to apprt/embedded.zig); see prompt-surface.zig.
export fn gav_ghostty_command_output(surface: *Surface, x: f64, y: f64, select: bool, output: [*]u8, capacity: usize) isize {
    const pixels = surface.cursorPosToPixels(.{ .x = @floatCast(x), .y = @floatCast(y) }) catch return -1;
    return surface.core_surface.commandOutputText(pixels, select, output[0..capacity]);
}

export fn gav_ghostty_prompt_click(surface: *Surface, x: f64, y: f64) bool {
    const pixels = surface.cursorPosToPixels(.{ .x = @floatCast(x), .y = @floatCast(y) }) catch return false;
    return surface.core_surface.promptClickMove(pixels);
}
