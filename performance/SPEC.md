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

## Matched AC acceptance (2026-10-17)

The 2026-10-17 matched AC comparison completed 44/44 successful attempts: one excluded warm-up and
ten measured launches per embedded/remote mode and build. Both clean source publishes (7e3753b0 and
ac98d2c8) used SDK 10.0.401, runtime 10.0.12, osx-arm64, self-contained non-composite ReadyToRun and
the identical runnable toolkit/signed fixture. Both were rebuilt consistently with compiled Discord
XAML present; the earlier inconsistent baseline was preserved and excluded. Every pre/post-launch
power observation was AC. No outlier was removed.

Embedded first-window time was 461.0 ± 13.6 → 434.9 ± 13.4 ms (−5.7%); first-content was
550.6 ± 8.5 → 536.1 ± 12.5 ms and workspace-ready 804.4 ± 22.9 → 774.3 ± 16.5 ms. Remote
user-launch first-window time was 663.8 ± 7.0 → 639.9 ± 23.4 ms (−3.6%), including fresh host
startup; UI-only time was 454.5 ± 6.2 → 423.2 ± 11.1 ms (−6.9%). Remote total first-content
was 764.7 ± 11.5 → 746.3 ± 27.9 ms and workspace-ready 1035.3 ± 24.6 → 989.7 ± 31.7 ms.
These are means ± sample SD, n=10 per mode/build. Initial physical-footprint means increased:
embedded 237.4 ± 8.9 → 239.0 ± 7.4 MiB; remote combined UI/host trees 327.3 ± 9.1 →
329.4 ± 9.5 MiB (both about +0.7%). The experiment shows no RAM reduction.

The largest elapsed-phase change was main-thread profile/host setup (−47.6 ms embedded, −30.0 ms
remote). Bootstrap increased by 12.6/15.8 ms while resources decreased by 12.0/11.8 ms; moved work
and concurrency change these intervals' contents. Construction was effectively unchanged
(−1.1/+0.1 ms). Embedded showing/OS observation increased by 24.3 ms; remote decreased by 3.8 ms.
The preparation worker's separately drawn span overlaps the sequential checkpoints and is not added
to them. These boundaries do not establish pure CPU, total native/Skia/GPU cost or exact presentation.

The standalone comparison and unchanged raw observations remain in review workspace-21 under
`artifacts/performance/ac-comparison`, `ac-baseline-measured` and `ac-candidate-measured`; matched
binary archives and manifest hashes are retained alongside them. Actual Chromium rendering at
1280/1440 px passed eight SVG charts, unclipped labels, no page overflow, no script errors or external
requests, and embedded raw-data downloads. Batches ran serially on the shared development Mac with
warm filesystem caches, without randomized ordering, CPU affinity or desktop/thermal isolation.
Differences are descriptive, not a significance test or large-project/restored-session acceptance.

## ReadyToRun tuning research (2026-10-16)

The initial 2026-10-16 first-window candidate retains the original clean 7e3753b0 publish under
`artifacts/performance/baseline-build` in its review workspace. Both SDK and runtime provenance are
10.0.401 / runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64/10.0.12. Actual power checks remained on
battery, so no before/after benchmark launch, timing, initial RAM or comparison report was produced.
The original baseline Discord UI assembly lacked compiled XAML; a later targeted rebuild restored it
without source changes. A controlled comparison must produce a consistently built clean baseline or
explicitly retain that binary difference as a confound. The preserved baseline has not been rewritten.

No tuning option below has been enabled or benchmarked by the first-window source changes. The initial
publish contract was self-contained, non-composite ReadyToRun with tiered compilation/PGO; NativeAOT
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

## Composite ReadyToRun default (2026-10-17)

The shared build properties default to composite when not explicitly overridden.
Desktop macOS and Windows publishers and the profiling publisher now explicitly use
`PublishReadyToRun=true` and `PublishReadyToRunComposite=true` for UI/host outputs.
The macOS packaged test harness remains non-composite R2R so its code can use shipped
component layouts when staged beside the UI; separately compiled composite groups cannot
be treated as interchangeable assembly sets. Windows checks retain their own composite set.
Tiered compilation and
tiered PGO remain enabled; trimming and NativeAOT remain disabled. Normal source builds
are unchanged. The measured startup/footprint tradeoff motivated this explicit choice.

The isolated follow-up used the same experimental source, SDK 10.0.401/runtime 10.0.12,
osx-arm64 and embedded fresh-profile fixture as the earlier non-composite/NativeAOT runs.
One warm-up plus ten measured AC launches all succeeded. Means ± sample SD for
non-composite → composite: first window 463.1 ± 22.8 → 401.8 ± 32.6 ms; first content
560.7 ± 53.6 → 441.2 ± 43.6 ms; initial workspace ready 767.6 ± 29.8 → 715.8 ± 42.2 ms;
physical footprint five seconds later 236.3 ± 8.6 → 321.8 ± 6.0 MiB. Earlier NativeAOT
was 217.0 ± 12.7 ms / 163.5 ± 0.6 MiB, with unresolved compatibility limits.
Composite's native image was 171,883,008 bytes. Logical app bundle size rose 245.7 →
309.4 MiB. Comparisons are descriptive: runs were not contemporaneous or randomized.

The experiment included generated serializer adaptations that are not applied to main.
A legacy partial-settings regression, unresolved AOT/trim diagnostics, unsupported
dynamic external plugins under NativeAOT and EventStream startup failures limit that
experiment's acceptance. It is not a production NativeAOT candidate. The standalone
three-way report, raw observations, binary hashes and diagnostic evidence are preserved
in the main checkout's ignored `artifacts/performance/composite-comparison` and adjacent
measurement directories before deleting the NativeAOT experiment workspace/branch.
Production source and defaults must be verified separately from those experimental binaries.

Production composite UI/host publishing and the versionable macOS harness passed the packaged
gate: 139 cases across two lanes, with `SHARPRAIL_TEST_GIT_SOURCE` enabled. The strict Release
build and touched formatting passed. MSBuild evaluation confirmed composite/tiering/PGO defaults
are true and the harness override is false. The default property was added after the gate with
unchanged effective explicit publish flags; the final compiler/property checks cover that addition.
Windows runtime acceptance was not run on macOS.
