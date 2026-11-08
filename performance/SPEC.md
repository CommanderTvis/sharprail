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

## Packaging

NativeAOT replaces composite ReadyToRun for the app and the remote host in every package and in the profiling publisher
(`NativeAot.props`; [Plugin security](../src/SharpRail.Plugins.Api/Security.SPEC.md) gives the reason). The checks publish
non-composite ReadyToRun. Dated measurements are not kept here: rerun the toolkit for a number.
