# Ghostty renderer benchmarks

Saved source for the NSView (`native`) versus direct Metal texture (`texture`)
measurements. NSView remains a library API even though SharpRail uses texture.
These standalone harnesses exclude the workbench and terminal relay.

Build from the repository root on macOS with Xcode tools installed:

```sh
sh benchmarks/ghostty/build.sh
```

Run only when measurements are requested, with an unlocked desktop, AC power,
and a 2× display. Each output directory must be new; its parent must exist.

```sh
caffeinate -diu node benchmarks/ghostty/run.mjs memory texture .bench/texture-memory
caffeinate -diu node benchmarks/ghostty/run.mjs presentation texture .bench/texture-presentation
caffeinate -diu node benchmarks/ghostty/run.mjs attribution texture .bench/texture-inventory
node benchmarks/ghostty/summarize.mjs .bench/texture-memory
```

Replace `texture` with `native` to measure the library NSView control. Memory and
presentation each run three processes; attribution runs one. Presentation uses
ScreenCaptureKit restricted to the benchmark's own window and requires screen
recording access. Capture starts after the CPU/RAM phases.

The workload uses 960×540 logical pixels, 119×30 cells, 480 redraws at 60 Hz and
three approximately 64 MiB ANSI floods. Memory additionally measures settlement
and terminal disposal, using `TASK_VM_INFO.phys_footprint` rather than RSS alone.
Presentation records 40 PTY-output-to-WindowServer samples after eight warmups.
Power is checked every second and a trial has a 120-second timeout.

Attribution saves live Metal resources, stacks, `vmmap` and `footprint`. Its
instrumentation changes CPU/RAM behavior; do not use those totals as benchmark
results. `native/IOTrace.c` optionally traces allocation-related IOKit calls via
`DYLD_INSERT_LIBRARIES` and `TRACE_IO_PATH`. MetalTrace's experimental `TRACE_*`
flags include copy suppression and queue sharing: keep them unset for valid
rendering and ordinary comparisons.

`results-2026-10-01.json` preserves the final aggregate measurements and saved
baselines. Existing baseline data should be reused for comparisons. Generated
binaries, logs, captures and machine-specific maps are not versioned.
