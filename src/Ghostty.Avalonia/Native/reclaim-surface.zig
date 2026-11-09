
// Asks the render thread to free the render targets of an occluded surface (Reclaim.patch); appended to Surface.zig.
pub fn gavReclaimTargets(self: *Surface) void {
    _ = self.renderer_thread.mailbox.push(global.io(), .{ .gav_reclaim = {} }, .{ .forever = {} });
    self.renderer_thread.wakeup.notify() catch {};
}
