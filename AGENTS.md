# SharpRail instructions

## Startup performance sanity

Keep the path from process launch to the first rendered, interactive workspace minimal. Establish the workspace identity before restoring its documents, then load independent panel data progressively.

- Do not gate workspace mounting, document restoration, or basic input on a full Git snapshot: status, diffs, line counts, untracked-file reads, branch enumeration, and worktree enumeration belong in deferred refreshes. Fetch only the minimal identity information needed for correct workspace routing before mounting.
- Keep recursive filesystem scans, spec indexing, network requests, nonessential profile writes, and eager construction of hidden panels or inactive documents off the startup critical path and UI thread. Bound initial file enumeration to what the visible UI needs.
- An async method can still execute synchronous work on the UI thread. Move blocking I/O and substantial parsing to background execution; apply control and layout changes on the dispatcher. Awaiting background work before mounting still makes it a startup dependency.
- Keep project-switch synchronization for correctness, but do not hold the project-switch gate across optional Git or indexing refreshes. Cancel superseded work and reject results for an outdated workspace before applying them.
- Reuse the initial control tree where practical; avoid building, clearing, and rebuilding the same panels during startup. Restore visible documents first and defer inactive content.
- Apply deferred data updates to the affected panel contents without rebuilding dock chrome or unrelated content. Preserve tab instances, keyboard focus, and pointer targets when Git or indexing results arrive.
- Treat window creation, first presented frame, and usable workspace content as separate milestones. A layout callback or readiness log alone does not prove pixels were presented or restored content is ready.
- Verify rendering backend and runtime publish settings before tuning Skia caches or changing JIT/GC options. Attribute delays to measured stages; do not claim an improvement from configuration alone. Run benchmarks only when explicitly requested.

For startup changes, verify fresh-profile and restored-profile behavior, project-switch races, and usability while deferred data is still loading. Git or indexing failures must not prevent otherwise accessible workspace files from being opened.
