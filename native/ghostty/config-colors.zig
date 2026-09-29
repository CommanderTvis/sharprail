
// The pinned embedding API can read configuration, but exposes no color setter.
export fn sr_ghostty_config_colors(self: *Config, background: u32, foreground: u32) void {
    self.background = .{ .r = @truncate(background >> 16), .g = @truncate(background >> 8), .b = @truncate(background) };
    self.foreground = .{ .r = @truncate(foreground >> 16), .g = @truncate(foreground >> 8), .b = @truncate(foreground) };
}
