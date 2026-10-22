// Snapshot only the hovered logical line, with a viewport-cell index for each Unicode scalar.
export fn gav_ghostty_link_line(surface: *Surface, x: f64, y: f64, text: [*]u32, cells: [*]u32, capacity: usize, info: [*]f64) usize {
    const core = &surface.core_surface;
    const pixels = surface.cursorPosToPixels(.{ .x = @floatCast(x), .y = @floatCast(y) }) catch return 0;
    core.renderer_state.mutex.lock();
    defer core.renderer_state.mutex.unlock();
    const term = &core.io.terminal;
    const pages = &term.screen.pages;
    const grid = core.size.grid();
    if (pixels.x < @as(f64, @floatFromInt(core.size.padding.left)) or pixels.y < @as(f64, @floatFromInt(core.size.padding.top))) return 0;
    const point = core.posToViewport(pixels.x, pixels.y);
    if (point.x >= grid.columns or point.y >= grid.rows) return 0;
    const scale = surface.content_scale;
    info[0] = @floatFromInt(grid.columns);
    info[1] = @floatFromInt(point.y * @as(u32, grid.columns) + point.x);
    info[2] = @as(f64, @floatFromInt(core.size.cell.width)) / scale.x;
    info[3] = @as(f64, @floatFromInt(core.size.cell.height)) / scale.y;
    info[4] = @as(f64, @floatFromInt(core.size.padding.left)) / scale.x;
    info[5] = @as(f64, @floatFromInt(core.size.padding.top)) / scale.y;
    var row = point.y;
    while (row > 0) {
        const previous = pages.getCell(.{ .viewport = .{ .x = 0, .y = row - 1 } }) orelse break;
        if (!previous.row.wrap) break;
        row -= 1;
    }
    var count: usize = 0;
    while (row < grid.rows) : (row += 1) {
        const first = pages.getCell(.{ .viewport = .{ .x = 0, .y = row } }) orelse break;
        var col: @TypeOf(point.x) = 0;
        while (col < grid.columns) : (col += 1) {
            const cell = pages.getCell(.{ .viewport = .{ .x = col, .y = row } }) orelse break;
            if (cell.cell.wide == .spacer_tail or cell.cell.wide == .spacer_head) continue;
            if (count >= capacity) return 0;
            const index = @as(u32, row) * grid.columns + col;
            if (cell.cell.wide == .wide and info[1] == @as(f64, @floatFromInt(index + 1))) info[1] -= 1;
            text[count] = if (cell.cell.codepoint() == 0) ' ' else cell.cell.codepoint();
            cells[count] = index;
            count += 1;
            if (cell.cell.hasGrapheme()) {
                for (cell.node.data.lookupGrapheme(cell.cell).?) |cp| {
                    if (count >= capacity) return 0;
                    text[count] = cp;
                    cells[count] = index;
                    count += 1;
                }
            }
        }
        if (!first.row.wrap) break;
    }
    return count;
}

export fn gav_ghostty_context_link(surface: *Surface, x: f64, y: f64, shift: bool, output: [*]u8, capacity: usize) isize {
    const pixels = surface.cursorPosToPixels(.{ .x = @floatCast(x), .y = @floatCast(y) }) catch return 0;
    const core = &surface.core_surface;
    const grid = core.size.grid();
    if (pixels.x < @as(f64, @floatFromInt(core.size.padding.left)) or pixels.y < @as(f64, @floatFromInt(core.size.padding.top))) return 0;
    const point = core.posToViewport(pixels.x, pixels.y);
    if (point.x >= grid.columns or point.y >= grid.rows) return 0;
    return core.contextLink(pixels, shift, output[0..capacity]);
}

// Replace the file-link cells the renderer underlines. `cells` are viewport indexes (row * columns + column) read by
// gav_ghostty_link_line, `codepoints` the base codepoint each showed. Cells whose text already moved are dropped; the
// renderer keeps the rest in screen coordinates and underlines them in the same frame as their text.
export fn gav_ghostty_set_link_underlines(surface: *Surface, cells: [*]const u32, codepoints: [*]const u32, count: usize) bool {
    const core = &surface.core_surface;
    {
        core.renderer_state.mutex.lock();
        defer core.renderer_state.mutex.unlock();
        const term = &core.io.terminal;
        const pages = &term.screen.pages;
        const links = &core.renderer.gav_links;
        links.clearRetainingCapacity();
        const top_pin = pages.pin(.{ .viewport = .{} }) orelse return false;
        const top = (pages.pointFromPin(.screen, top_pin) orelse return false).screen.y;
        for (0..count) |i| {
            const x = cells[i] % pages.cols;
            const y = cells[i] / pages.cols;
            if (y >= pages.rows) continue;
            const cell = pages.getCell(.{ .viewport = .{ .x = @intCast(x), .y = @intCast(y) } }) orelse continue;
            const codepoint: u21 = if (cell.cell.codepoint() == 0) ' ' else cell.cell.codepoint();
            if (codepoint != codepoints[i]) continue;
            links.append(core.renderer.alloc, .{ .screen = term.active_screen, .x = @intCast(x), .y = top + y, .codepoint = codepoint }) catch return false;
        }
    }
    core.renderer_thread.wakeup.notify() catch {};
    return true;
}
