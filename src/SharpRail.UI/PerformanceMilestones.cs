using System.Diagnostics;
using System.Text.Json;

using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace SharpRail.UI;

internal static class StartupPhases
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPRAIL_PERFORMANCE") == "1";
    private static readonly (string Name, long Timestamp)[] Pending = Enabled ? new (string, long)[32] : [];
    private static int count;
    private static int flushed;

    internal static void Instrument(Avalonia.AppBuilder builder)
    {
        if (!Enabled) return;
        if (builder.WindowingSubsystemInitializer is { } windowing)
            builder.UseWindowingSubsystem(Observe(windowing, "windowing-init"), builder.WindowingSubsystemName ?? "");
        if (builder.RenderingSubsystemInitializer is { } rendering)
            builder.UseRenderingSubsystem(Observe(rendering, "skia-init"), builder.RenderingSubsystemName ?? "");
        if (builder.RuntimePlatformServicesInitializer is { } runtime)
            builder.UseRuntimePlatformSubsystem(Observe(runtime, "platform-services-init"), builder.RuntimePlatformServicesName ?? "");
        if (builder.TextShapingSubsystemInitializer is { } shaping)
            builder.UseTextShapingSubsystem(Observe(shaping, "text-shaping-init"), builder.TextShapingSubsystemName ?? "");
    }

    private static Action Observe(Action initialize, string phase) => () =>
    {
        Mark(phase + "-start");
        initialize();
        Mark(phase + "-end");
    };

    internal static void Mark(string name)
    {
        if (!Enabled) return;
        var timestamp = Stopwatch.GetTimestamp();
        lock (Pending)
        {
            for (var index = 0; index < count; index++) if (Pending[index].Name == name) return;
            if (count == Pending.Length) throw new InvalidOperationException("Startup phase buffer is full.");
            Pending[count++] = (name, timestamp);
        }
    }

    internal static void Flush()
    {
        if (!Enabled) return;
        lock (Pending)
        {
            while (flushed < count)
            {
                var (name, timestamp) = Pending[flushed++];
                Console.WriteLine("SHARPRAIL_PERF " + JsonSerializer.Serialize(new { milestone = name, timestamp, frequency = Stopwatch.Frequency, status = "ok" }));
            }
        }
    }
}

public sealed partial class WorkbenchWindow
{
    private readonly StartupMilestones? startupMilestones = StartupMilestones.Create();

    private async Task RecordFirstContentAsync(long request)
    {
        if (startupMilestones?.Matches(request) != true) return;
        try
        {
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
            if (compositor is null)
            {
                startupMilestones.Emit("first-content-rendered", "unavailable", "No composition visual.");
                return;
            }
            var batch = compositor.RequestCompositionBatchCommitAsync();
            await batch.Rendered.WaitAsync(lifetime.Token).ConfigureAwait(false);
            var timestamp = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                if (request == projectRequest && !lifetime.IsCancellationRequested)
                    startupMilestones.Emit("first-content-rendered", timestamp: timestamp);
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (request == projectRequest && !lifetime.IsCancellationRequested)
                    startupMilestones.Emit("first-content-rendered", "error", error.Message);
            });
        }
    }

    private sealed class StartupMilestones
    {
        private long? initialRequest;
        private bool files;
        private bool git;
        private bool failed;
        private readonly HashSet<string> emitted = [];

        internal static StartupMilestones? Create() => Environment.GetEnvironmentVariable("SHARPRAIL_PERFORMANCE") == "1" ? new() : null;
        internal void Begin(long request) => initialRequest ??= request;
        internal bool Matches(long request) => initialRequest == request;

        internal void Complete(long request, string stage, string? error = null)
        {
            if (!Matches(request)) return;
            if (stage == "files" ? files : git) return;
            if (stage == "files") files = true;
            else git = true;
            failed |= error is not null;
            Emit(stage + "-ready", error is null ? "ok" : "error", error);
            if (files && git) Emit("workspace-ready", failed ? "error" : "ok");
        }

        internal void Emit(string milestone, string status = "ok", string? detail = null, long? timestamp = null)
        {
            if (!emitted.Add(milestone)) return;
            var at = timestamp ?? Stopwatch.GetTimestamp();
            StartupPhases.Flush();
            Console.WriteLine("SHARPRAIL_PERF " + JsonSerializer.Serialize(new
            {
                milestone,
                timestamp = at,
                frequency = Stopwatch.Frequency,
                status,
                detail
            }));
        }
    }
}