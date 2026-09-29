// The pinned embedding API can read configuration, but exposes no color setter.
// colors holds 0xAARRGGBB values: background, foreground, cursor, selection background,
// selection foreground, then the 16 ANSI colors. A zero alpha leaves an optional color unset.
export fn sr_ghostty_config_colors(self: *Config, colors: [*]const u32, minimum_contrast: f64) void {
    self.background = srColor(colors[0]);
    self.foreground = srColor(colors[1]);
    self.@"cursor-color" = srOptionalColor(colors[2]);
    self.@"selection-background" = srOptionalColor(colors[3]);
    self.@"selection-foreground" = srOptionalColor(colors[4]);
    for (0..16) |index| {
        const color = srColor(colors[5 + index]);
        self.palette.value[index] = .{ .r = color.r, .g = color.g, .b = color.b };
    }
    self.@"minimum-contrast" = minimum_contrast;
}

fn srColor(value: u32) Config.Color {
    return .{ .r = @truncate(value >> 16), .g = @truncate(value >> 8), .b = @truncate(value) };
}

fn srOptionalColor(value: u32) ?Config.TerminalColor {
    return if (value >> 24 == 0) null else .{ .color = srColor(value) };
}
