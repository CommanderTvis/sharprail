using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace SharpRail.UI.Rendering;

// Per-hunk realization for stacked Changes review sections. Each hunk is a slot of its own; only slots near the
// outer viewport hold editors. A slot that was never drawn has an estimated height, and a released slot keeps the
// height it was measured at, so the review's extent stays stable and its scroll anchor corrects the remainder.
internal sealed partial class DiffView
{
    /// <summary>The height a hunk row is estimated at before its slot has been measured.</summary>
    internal const double EstimatedRowHeight = 18;

    private sealed class HunkSlot(Border host, Func<Control> build, int rows)
    {
        internal readonly Border Host = host;
        internal readonly Func<Control> Build = build;
        internal readonly double Estimate = rows * EstimatedRowHeight + 12;
        internal readonly List<EditorFrame> Frames = [];
        internal double Measured = double.NaN;
        internal bool Realized;
    }

    private readonly List<HunkSlot> slots = [];
    private Rect hunkViewport;
    private bool hunksQueued;

    /// <summary>How many hunk slots this view has, and how many hold editors now; zero slots means one undivided body.</summary>
    internal (int Slots, int Realized) HunkRealization => (slots.Count, slots.Count(slot => slot.Realized));

    /// <summary>The hunk slots, top to bottom, that a surrounding scroller may anchor its position to.</summary>
    internal IEnumerable<Control> AnchorCandidates => slots.Select(slot => slot.Host);

    /// <summary>The slot heights a reader would see now: measured where known, estimated otherwise.</summary>
    internal IReadOnlyList<(double Height, bool Measured)> HunkHeights =>
        [.. slots.Select(slot => (slot.Host.Bounds.Height, !double.IsNaN(slot.Measured)))];

    private void RenderHunks(List<string> preamble, List<Hunk> hunks, bool sideBySide)
    {
        var stack = new StackPanel { Name = "DiffHunks" };
        Hunk? previous = null;
        for (var index = 0; index < hunks.Count; index++)
        {
            var hunk = hunks[index];
            var before = previous;
            var lead = index == 0 && !sideBySide ? preamble : [];
            var rows = lead.Count + (Gap(before, hunk) > 0 ? 1 : 0) + (sideBySide ? 0 : 1) + hunk.Lines.Count;
            var host = new Border { Name = "DiffHunk", Tag = index };
            var slot = new HunkSlot(host, () => sideBySide ? SplitBody([hunk], before) : InlineBody(lead, [hunk], before), rows);
            host.Height = slot.Estimate;
            host.SizeChanged += (_, args) => { if (slot.Realized && args.NewSize.Height > 0) slot.Measured = args.NewSize.Height; };
            slots.Add(slot);
            stack.Children.Add(host);
            previous = hunk;
        }
        Ui.Place(body, stack);
        QueueHunks();
    }

    private void ClearHunks()
    {
        slots.Clear();
        hunksQueued = false;
    }

    private void QueueHunks()
    {
        if (hunksQueued || slots.Count == 0) return;
        hunksQueued = true;
        Dispatcher.UIThread.Post(() => { hunksQueued = false; RealizeHunks(); }, DispatcherPriority.Loaded);
    }

    private void OnEffectiveViewport(object? sender, EffectiveViewportChangedEventArgs args)
    {
        hunkViewport = args.EffectiveViewport;
        if (slots.Count > 0) QueueHunks();
    }

    // Slots within one viewport of the visible part realize; slots beyond two release their editors.
    private void RealizeHunks()
    {
        if (disposed || slots.Count == 0 || VisualRoot is null) return;
        var viewport = hunkViewport;
        if (viewport.Height <= 0 || double.IsInfinity(viewport.Height)) viewport = new Rect(Bounds.Size);
        if (viewport.Height <= 0) return;
        var changed = false;
        foreach (var slot in slots)
        {
            if (slot.Host.TranslatePoint(default, this) is not { } point) continue;
            var top = point.Y; var bottom = top + slot.Host.Bounds.Height;
            var near = top < viewport.Bottom + viewport.Height && bottom > viewport.Top - viewport.Height;
            var far = top > viewport.Bottom + 2 * viewport.Height || bottom < viewport.Top - 2 * viewport.Height;
            if (near && !slot.Realized) { Realize(slot); changed = true; }
            else if (far && slot.Realized) { Release(slot); changed = true; }
        }
        if (changed) QueueHunks();
    }

    private void Realize(HunkSlot slot)
    {
        var first = frames.Count;
        var content = slot.Build();
        slot.Frames.AddRange(frames.Skip(first));
        slot.Realized = true;
        slot.Host.Child = content;
        slot.Host.MinHeight = double.IsNaN(slot.Measured) ? 0 : slot.Measured;
        slot.Host.Height = double.NaN;
        // The measured height replaces the cached one once the editors have laid out.
        Dispatcher.UIThread.Post(() => { if (slot.Realized) slot.Host.MinHeight = 0; }, DispatcherPriority.Background);
    }

    private void Release(HunkSlot slot)
    {
        var height = slot.Host.Bounds.Height;
        if (height > 0) slot.Measured = height;
        foreach (var frame in slot.Frames) { frames.Remove(frame); frame.Editor.Dispose(); }
        slot.Frames.Clear();
        slot.Realized = false;
        slot.Host.Child = null;
        slot.Host.MinHeight = 0;
        slot.Host.Height = double.IsNaN(slot.Measured) ? slot.Estimate : slot.Measured;
    }
}