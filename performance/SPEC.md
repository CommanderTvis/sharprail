---
id: module-performance
type: module-design
status: active
title: macOS startup and initial RAM profiling
---

# Performance toolkit

The toolkit observes the [UI startup boundaries](../src/SharpRail.UI/SPEC.md) and owns repeatable macOS launch experiments, diagnostic collection, provenance and reports;
it does not optimize the product or declare cross-platform acceptance. It runs published self-contained
ReadyToRun app/host outputs in a separate bundle, verified against a complete file-hash manifest.
Every launch uses isolated profile/state and the versioned signed two-file Git fixture. Runtime fixture
creation never bypasses signing. Warm-ups are excluded from aggregates but preserved as evidence.

WindowServer detection, render-thread content completion, workspace layout and initial file/Git
readiness are distinct boundaries. The app's opt-in marker contract uses monotonic timestamps; the
runner never infers a missing rendering or readiness stage from another marker. The marker overhead,
including an extra render-batch request, remains part of headline launches. Initial RAM is physical
footprint five seconds after readiness, with a 500 ms lateness tolerance; RSS is separately named.
Neither managed metrics nor process accounting establishes physical GPU residency.

Per-PID identity checks, process-tree membership, unreadable/excluded PIDs and actual sample timing
remain in raw evidence. Missing/unreadable roots or classified descendants invalidate a sample;
unclassified PIDs are excluded explicitly. Sampling is not an atomic system-wide snapshot. Errors,
failed attempts and outliers are retained. Only successful measured samples enter mean, sample
standard deviation, median and range. Baselines must match workload, machine, SDK/target runtime/RID and measurement
contract; differences are descriptive, without an implied significance test.

EventPipe collection is a separate instrumented run using pinned local tools. Managed thread samples
are not exact on-CPU/native/GPU attribution. Runtime counters begin after readiness, and neither
traces nor counters enter headline aggregates. Offline HTML carries charts, raw data and provenance;
no CDN or external account is required. Only the runner's isolated process groups and scratch data
are cleaned; user profiles, live app bundles and neighboring repositories are outside its write scope.

Startup reports use a common-scale segmented timeline between observed milestone means, with
endpoint sample SD and exact numeric tables. Independent milestones are ordered by observed mean,
not assumed dependencies; intervals are elapsed time rather than causal component attribution.
The report explicitly distinguishes measured boundaries from unmeasured CLR, Avalonia, Skia/Metal
and application costs, and describes the controlled baselines needed to investigate those costs.
Managed stack weights from the longer diagnostic run are never presented as a first-window breakdown.

`self-test` covers statistics, filtering, process attribution, raw JSON, baseline compatibility, report
escaping and managed-stack visualization. Real macOS measurement and diagnostic acceptance must be
recorded from actual generated outputs; unit checks alone do not verify native measurement or tools.

The 2026-10-16 macOS acceptance used self-contained Release/ReadyToRun app and host binaries with
runtime pack 10.0.12. One excluded warm-up and ten measured launches per embedded/remote mode all
succeeded on AC (22 total attempts); every initial files/Git readiness marker completed. Actual
snapshot delay and per-PID native accounting were retained in raw JSON. A separate real EventPipe
launch produced nettrace, runtime counters, hot methods and a Speedscope-derived managed stack chart;
process-group cleanup left no target or diagnostic tool running. These checks establish this small
fixture on the tested Mac, not large-project, cold-filesystem, other-machine or GPU-residency behavior.
Runtime pack metadata was derived from unchanged, manifest-hashed published deps.json after the run;
no timing or RAM observations were recollected or altered for that metadata addition.

The native process-tree acceptance also started an isolated shell with a live child, then verified both
PIDs appeared once, with the correct parent relationship and successful resource reads. These runs
used a shared development Mac without CPU affinity, desktop quiescence or thermal isolation;
background desktop activity may contribute to the retained variation. The complete source gate
passed all 139 cases across four lanes, alongside the focused startup checks and performance self-tests.

The v2 measurement contract records buffered early startup phases: managed entry, Avalonia's actual
windowing/rendering/runtime/text-shaping initialization callbacks, app resources, host/profile/plugin
composition and window construction/opening. Callback wrapping preserves the original initializer
and only runs in opt-in measurements. Early marks are timestamped without JSON output; the existing
workspace marker path flushes them later. The report partitions first-window wall time using paired
per-launch boundaries and shows callback timings separately as nested bootstrap costs, never added
twice. An incomplete or reversed boundary chain withholds the component chart. The pre-managed
envelope includes OS/process launch, runtime loading and entry preparation; it is not pure CLR time.
Skia platform initialization excludes native work deferred to window creation and drawing. Existing
v1 measurements remain readable, but are not compatible baselines for the extra instrumentation.

Additional opt-in diagnostic marks identify desktop preparation (which may overlap application resource
initialization), entry into the window constructor, compiled XAML completion and the first configured
dock build. The constructor-entry envelope includes native base construction and managed field
initializers; it is not pure WindowServer or native time. These supplementary marks do not redefine
the v2 first-window, first-content, layout, readiness or memory sampling contract.

## ReadyToRun tuning research (2026-10-16)

The first-window candidate retains the original clean 7e3753b0 publish under
`artifacts/performance/baseline-build` in its review workspace. Both SDK and runtime provenance are
10.0.401 / runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64/10.0.12. Actual power checks remained on
battery, so no before/after benchmark launch, timing, initial RAM or comparison report was produced.
The original baseline Discord UI assembly lacked compiled XAML; a later targeted rebuild restored it
without source changes. A controlled comparison must produce a consistently built clean baseline or
explicitly retain that binary difference as a confound. The preserved baseline has not been rewritten.

No tuning option below has been enabled or benchmarked by the first-window source changes. The current
publish contract remains self-contained, non-composite ReadyToRun with tiered compilation/PGO; NativeAOT
is excluded. Any future experiment needs separate binary provenance and the same startup/content/readiness/
RAM contract, plus restored documents, plugins and sustained-use acceptance.

- Composite R2R permits broader optimization and precompilation, at a cost in file size and publish time.
  Microsoft's recommendation focuses on Linux startup or disabled tiering, so this is not an established
  macOS improvement. Distribution also couples the composite assemblies. Selective
  `PublishReadyToRunExclude` can reduce image size/loading cost for cold assemblies, trading that for JIT
  work on first use. Keep plugin first-use latency in any comparison.
  [Official R2R deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run).
- Static startup PGO can convert a representative diagnostic trace into MIBC optimization data and feed
  it into Crossgen2. The design names `ReadyToRunOptimizationData`, but the installed 10.0.401 SDK
  actually consumes the `PublishReadyToRunPgoFiles` item and runtime-pack MIBC data. A future experiment
  must verify compiler inputs rather than copying the older property blindly. This requires a separately instrumented training run; the
  current headline samples and generic sampling trace are not automatically a suitable profile. The
  official design describes building dotnet-pgo from the runtime repository and supports merging profiles.
  Training must include embedded/remote, restored documents and plugin scenarios to avoid specializing
  only for the tiny fixture. Profile freshness and trace/assembly matching become build inputs.
  [Official dotnet-pgo design](https://github.com/dotnet/runtime/blob/main/docs/design/features/dotnet-pgo.md).
  [SDK Crossgen targets](https://github.com/dotnet/sdk/blob/v10.0.401/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.CrossGen.targets).
- Crossgen2 exposes size/speed preferences, partial images, method/file layout and instruction-set choices.
  These are compiler options, not evidence that a generic `OptimizationPreference` property improves
  R2R. Version-pin and inspect the deployed compiler before proposing them; smaller images may reduce
  paging but increase JIT/first-use cost, and host-specific instruction sets constrain distribution.
  The installed SDK exposes `PublishReadyToRunCrossgen2ExtraArgs`, but no extra argument is used here.
  [Official Crossgen2 options](https://github.com/dotnet/runtime/blob/main/src/coreclr/tools/aot/crossgen2/Crossgen2RootCommand.cs).
- Tiered compilation replaces hot R2R methods later, and dynamic PGO affects those JIT tiers. Existing
  tiering/PGO/quick-loop JIT settings are already enabled. Changing them would be an independent runtime
  experiment with startup, CPU and sustained-throughput tradeoffs, rather than a missing R2R switch.
  [Official compilation configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation).

The 2026-10-16 v2 AC batch succeeded in all 22 attempts (one excluded warm-up and ten measured
launches per mode). Embedded UI launch to first observed window was 454.9 ± 34.9 ms. Paired phases
were loader/managed entry 43.3 ms, Avalonia bootstrap 82.6 ms, resources/theme 40.4 ms, framework
completion 9.7 ms, host/profile/plugins 53.2 ms, menu/routing 0.1 ms, window construction 104.1 ms
and showing/OS observation 121.5 ms. Bootstrap contained native windowing 47.1 ms and Skia platform
initialization 14.7 ms; these are not additional costs. Remote UI-only first-window time was
456.2 ± 13.0 ms, excluding separate host startup. Construction and showing are the largest measured
investigation targets; no experiment yet establishes how much is removable or isolates pure CLR
and total Skia/GPU cost. The report includes ranked intervals, showing sub-boundaries and explicit
next profiling targets. Actual Chromium rendering verified both phase charts without hidden or
clipped SVG labels or page overflow. A preceding batch interrupted by a switch to battery is retained
separately and excluded, along with the incompatible v1 baseline. Published runtime lookup appends
the deps suffix to dotted executable names rather than replacing their final name component.
