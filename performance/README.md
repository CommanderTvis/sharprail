# macOS startup and initial RAM

This toolkit publishes a separate Release/composite ReadyToRun app and host, launches a reproducible
workspace with isolated state, measures startup and RAM, and writes raw observations plus an offline
HTML report. It does not replace `artifacts/SharpRail.app` or use your normal SharpRail profile.

Run from this checkout on macOS with the local .NET SDK, Xcode command-line tools, and AC power:

```sh
performance/run.sh publish
performance/run.sh measure
performance/run.sh diagnose
```

`DOTNET=/absolute/path/to/dotnet` selects an existing SDK when a worktree has no local SDK.
Publishing and diagnostic tool restoration require network access on first use. The diagnostic
manifest pins `dotnet-trace` and `dotnet-counters` locally; no global tools are installed.
Launching the actual graphical app needs a logged-in macOS desktop. WindowServer probing does
not capture the screen or require a screenshot of other applications.

The default measurement performs one excluded warm-up and ten fresh launches for each of embedded
and remote mode. Remote starts a new local host and waits for its readiness before launching the UI.
Each launch gets its own profile and host state. Agents are not launched and no terminal is created
implicitly. The tiny signed Git bundle in `fixture/` supplies the same two-file workspace; its bundle,
commit, tree and file hashes are verified. No runtime commit or signing override is needed.

```sh
performance/run.sh measure --warmups 1 --repetitions 10 --modes embedded,remote --memory-delay-ms 5000 --output artifacts/performance/current
performance/run.sh report --input artifacts/performance/current/results.json --baseline artifacts/performance/baseline/results.json --output artifacts/performance/comparison
performance/run.sh self-test
```

Use a new output directory per run. Outputs default to `artifacts/performance/<command>-<time>/`.
`publish` uses only `artifacts/performance/build/`. Measurement checks every published-file hash
against its build manifest and verifies ReadyToRun headers; a changed output requires a new publish.
The report contains the manifest, source commit/dirty state, SDK/RID and target runtime pack (from each published deps.json), machine, sample statuses and
per-process memory. Dirty builds are explicitly identified; they are not represented as clean commits.

## Measurement contract

`t0` is the harness's monotonic timestamp immediately before the first target launch. Milestones are
independent; their order is not assumed:

- `first-window`: WindowServer first lists an onscreen normal window at least 300 × 200. The native
  probe polls every 10 ms, starting after process launch; helper startup can add detection latency.
  This is not the first painted pixel.
- `first-content-rendered`: completion of the first workbench content composition batch is observed after it rendered.
  Scheduling latency is included; this is not an exact render-thread timestamp or physical screen presentation. If rendering is unavailable or fails, the
  raw marker retains that status and no successful time is invented.
- `workspace-layout`: workspace mounting and the layout pass completed; files and Git may still load.
- `workspace-ready`: the initial file cache and Git snapshot/branch catalogue have been applied.
  Gitless workspaces can complete successfully. Errors invalidate the sample. Background workspace
  statistics, spec scans, agent initialization and arbitrary document loading are outside this boundary.

RAM is measured five seconds **after workspace-ready**, not five seconds after launch. Actual
snapshot time is retained; a snapshot more than 500 ms late is invalid. The native probe reads
`proc_pid_rusage` physical footprint and resident bytes per target and live descendant, validating
process identity around each read. Inaccessible/missing targets invalidate the sample. PIDs whose
parent information could not be classified are retained as exclusions; short-lived descendants that
exit before the snapshot are not included. Snapshots are not atomic across PIDs. Neither memory
measure proves physical GPU residency.

Runtime/profiler injection overrides (`COMPlus_*`, `CORECLR_*`, `COR_*`, `DYLD_*` and runtime-affecting `DOTNET_*`) are rejected before launch; variable values are withheld. Routine SDK/CLI location settings are allowed.

Headline launches enable lightweight milestone logging and one extra render-batch request. They
exclude EventPipe tracing and counters, whose overhead would contaminate startup/RAM comparisons.
Filesystem caches are warmed, not flushed. AC is checked before and after each launch. This small
fixture is a reproducible regression workload, not a claim about all user projects.

All observations, failed attempts and warm-ups remain in `results.json`; only successful measured
samples enter aggregates. Means, sample standard deviations, medians and ranges are displayed.
The first-window chart partitions measured wall time from UI launch through managed entry, Avalonia bootstrap, app resources/services, window construction and OS observation. A nested table measures Avalonia’s windowing, Skia and text-shaping initialization callbacks without adding them twice. These boundaries locate expensive phases; they do not isolate pure CLR or total Skia/GPU cost. Early marks are buffered until later workspace logging. The separate milestone chart retains endpoint uncertainty and a report section explains controlled baselines still needed. The v2 instrumentation contract cannot be compared with v1 baselines. Single-sample standard deviation is unavailable. Baseline comparisons require the same schema,
measurement contract, machine, SDK, target runtime, RID, workload, modes and RAM delay; they are descriptive rather
than significance tests. Optional `report --max-startup-regression-percent N` and
`--max-memory-regression-percent N` fail when the workspace-ready or total physical-footprint mean, respectively, increases beyond that percentage;
these opt-in limits require `--baseline` and are not statistical significance tests. CLI output explains each gate and `comparison.json` retains its verdict. Incomplete or failed measured launches/warm-ups invalidate gates rather than passing on the successful subset. Historical report numbers from other projects are not imported as baselines.

## Diagnostic outputs

`diagnose` performs a separate embedded launch using the same published binaries and fixture:

- `startup.nettrace`: startup EventPipe trace with managed thread sampling and common runtime events.
- `startup.speedscope.json`: converted managed stack samples, suitable for Speedscope.
- `hot-methods.txt`: the .NET trace tool's top-method report.
- `runtime-counters.json`: five seconds of runtime counters starting after workspace readiness.
- `report.html`: managed stack visualization, hot methods, caveats and artifact links.
- `diagnostic.json`, `trace.log`, `counters.log`: provenance and original collection output.

Startup tracing can suspend the runtime and adds observer overhead. Managed thread sampling includes
waiting threads; it is not precise on-CPU time, native stack profiling or a GPU memory measurement.
GC heap counters and working set are not substituted for physical footprint. Heap dumps are deliberately
outside this startup/RAM workflow because collection can force a GC. The trace report describes the
instrumented run only and is never merged into measurement aggregates.

The runner terminates only its own isolated target process groups, including the traced app, and
removes temporary profiles and workspace copies. It retains requested reports and raw evidence.

Primary tool references: [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace),
[dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters),
[runtime metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime),
[macOS resource accounting](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/resource.h).

The runner does not enforce CPU affinity, desktop quiescence or thermal isolation. Results from a shared development Mac are exploratory baselines; background desktop activity can contribute to variation.
