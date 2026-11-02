
// Frees an occluded surface's render targets on its render thread (appended to apprt/embedded.zig); see Reclaim.patch.
export fn gav_ghostty_reclaim_targets(surface: *Surface) void {
    surface.core_surface.gavReclaimTargets();
}
