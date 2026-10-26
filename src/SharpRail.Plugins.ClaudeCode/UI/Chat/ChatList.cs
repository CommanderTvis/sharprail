using System.Collections.Specialized;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

internal interface IChatRowActions
{
    void Activate(ChatRow row, ChatHitKind kind, string? arg = null);
}

/// <summary>
/// The transcript, drawn like the kit's trees: one surface the height of the whole conversation inside a scroll viewer, and only the rows
/// in (or just beside) the viewport are laid out and drawn; no row owns a control. A row's height is its measured height once it has been
/// laid out at this width (kept by item, revision and width) and <see cref="ChatRowEstimates"/> until then, so the scrollbar is sized from
/// guesses that are refined as rows come into view, and the row being read stays where it is while that happens: the reading band, a line
/// inset from the viewport's top (after upstream's turn inset), names the row and the offset into it that scrolling restores.
/// </summary>
internal sealed class ChatList : ScrollViewer
{
    private const int HeightLimit = 200_000;
    private const int KeptLayouts = 6;
    private readonly AvaloniaList<ChatRow> rows;
    private readonly IChatRowActions actions;
    private readonly Surface surface;
    private readonly Dictionary<(string Id, int Revision, int Width), double> measured = [];
    private readonly List<double> current = [];
    private readonly Dictionary<(string Id, int Revision, int Width), ChatRowLayout> layouts = [];
    private readonly List<(string Id, int Revision, int Width)> recent = [];
    private readonly List<(ChatRow Row, int Index)> visible = [];
    private double[] tops = [0];

    private bool topsDirty = true;
    private double width = 600;
    private string? anchorId;
    private double anchorIntra;
    private bool programmatic;
    private bool passScheduled;
    private bool followScheduled;
    private int hovered = -1;
    private (string RowId, ChatHit Hit)? hoverHit;
    private ChatHit? focusStop;
    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    private string? selectedId;
    private (string Id, double Delta)? remembered;
    private (string Id, int Offset)? anchorAt, focusAt;
    private bool selecting, dragged;
    private Point pressedAt;
    private double pointerX, pointerY;
    private (ChatRow Row, string? Arg)? pendingLink;
    private DispatcherTimer? edgeTimer;
    private readonly Dictionary<(string Id, int Revision), string> rowTexts = [];

    internal ChatList(ChatTranscript transcript, IChatRowActions actions)
    {
        this.actions = actions;
        rows = transcript.Rows;
        Name = "ChatTranscript";
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        AutomationProperties.SetName(this, "Conversation");
        surface = new Surface(this);
        Content = surface;
        rows.CollectionChanged += OnRows;
        Reset();
        ScrollChanged += Scrolled;
    }

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// <summary>True while the view follows the newest item; scrolling up lets go, reaching the bottom (or "jump to latest") takes hold again.</summary>
    internal bool Stuck { get; private set; } = true;

    internal event Action<bool>? StuckChanged;

    /// <summary>Raised when the viewport moves, so a tall reply redraws the blocks that came into range.</summary>
    internal event Action? ViewportMoved;

    internal int RealizedRows => visible.Count;

    /// <summary>Settling passes run so far: a size change should cost one or two.</summary>
    internal int Passes { get; private set; }

    internal int CachedHeights => measured.Count;

    /// <summary>Frames drawn, and those drawn while the surface was already arranged at another width than the rows were laid out for; the second must stay zero.</summary>
    internal int Frames { get; private set; }

    internal int StaleFrames { get; private set; }

    internal ScrollViewer Scroller => this;

    internal double TotalHeight { get { EnsureTops(); return tops[^1]; } }

    /// <summary>The rows laid out in the last pass, with their top relative to the viewport and the height the list currently gives them.</summary>
    internal IReadOnlyList<(ChatRow Row, double Top, double Height)> VisibleRows()
    {
        EnsureTops();
        return [.. visible.Select(pair => (pair.Row, tops[pair.Index] - Offset.Y, current[pair.Index]))];
    }

    internal double RowTop(string id)
    {
        EnsureTops();
        foreach (var (row, index) in visible)
            if (row.Id == id) return tops[index] - Offset.Y;
        return double.NaN;
    }

    internal double EstimatedHeightOf(string id) => rows.ToList().FindIndex(row => row.Id == id) is var index and >= 0 ? current[index] : double.NaN;

    internal static string Describe(ChatItem item) => item switch
    {
        ChatUserItem user => "You: " + user.Text,
        ChatAssistantItem assistant => "Claude: " + assistant.Text,
        ChatThinkingItem => "Thinking",
        ChatActivityItem activity => "Activity: " + ChatRowLayout.Breadcrumb(activity.Steps),
        ChatToolItem tool => tool.Name + " " + tool.Summary + " " + tool.State,
        ChatPermissionItem permission => "Permission: " + permission.ToolName + " " + permission.Decision,
        ChatNoticeItem notice => notice.Text,
        ChatQuestionItem question => "Question: " + string.Join(" ", question.Questions.Select(item => item.Question)),
        _ => ""
    };

    // The line below the viewport's top that names what is being read, after upstream's turn inset: a tenth of the viewport, 48 to 80 pixels.
    private double Inset => Math.Clamp(ViewportHeight * 0.1, 48, 80);

    private double ViewportHeight => base.Viewport.Height > 0 ? base.Viewport.Height : surface.Bounds.Height;

    // ---- rows and heights

    private double HeightFor(ChatRow row) => measured.TryGetValue((row.Id, row.Revision, (int)Math.Round(width)), out var known) ? known : ChatRowEstimates.Estimate(row, width);

    private void Reset()
    {
        current.Clear();
        foreach (var row in rows) current.Add(HeightFor(row));
        topsDirty = true;
    }

    private void OnRows(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var k = 0; k < e.NewItems!.Count; k++) current.Insert(e.NewStartingIndex + k, HeightFor((ChatRow)e.NewItems[k]!));
                break;
            case NotifyCollectionChangedAction.Remove:
                current.RemoveRange(e.OldStartingIndex, e.OldItems!.Count);
                break;
            case NotifyCollectionChangedAction.Replace:
                for (var k = 0; k < e.NewItems!.Count; k++) current[e.NewStartingIndex + k] = HeightFor((ChatRow)e.NewItems[k]!);
                break;
            default:
                Reset();
                break;
        }
        topsDirty = true;
        SchedulePass();
    }

    private void EnsureTops()
    {
        if (!topsDirty && tops.Length == current.Count + 1) return;
        if (tops.Length != current.Count + 1) tops = new double[current.Count + 1];
        var sum = 0.0;
        for (var i = 0; i < current.Count; i++) { tops[i] = sum; sum += current[i]; }
        tops[^1] = sum;
        topsDirty = false;
    }

    private int RowAt(double y)
    {
        EnsureTops();
        if (current.Count == 0) return -1;
        int low = 0, high = current.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (tops[mid] <= y) low = mid; else high = mid - 1;
        }
        return low;
    }

    // The rows in and near the viewport, laid out at the current width before the first frame: their heights are exact, not estimates to be
    // corrected by a second pass. Twice, since the first corrections move the tops the range is taken from.
    private void Prelayout()
    {
        if (rows.Count == 0) return;
        var w = (int)Math.Round(width);
        for (var round = 0; round < 2; round++)
        {
            EnsureTops();
            var from = Offset.Y - ViewportHeight / 4;
            var to = Offset.Y + ViewportHeight * 1.25;
            var changed = false;
            for (var i = Math.Max(0, RowAt(from)); i < rows.Count && tops[i] <= to; i++)
            {
                var row = rows[i];
                var key = (row.Id, row.Revision, w);
                var layout = LayoutOf(row, key);
                measured[key] = layout.Height;
                if (Math.Abs(layout.Height - current[i]) > 0.5) { current[i] = layout.Height; changed = true; }
            }
            if (!changed) break;
            topsDirty = true;
        }
        EnsureTops();
    }

    private int IndexOfId(string id)
    {
        for (var i = 0; i < rows.Count; i++) if (rows[i].Id == id) return i;
        return -1;
    }

    // One pass per frame at most: new tops, a new extent, then the reading row or the end put back where they belong.
    private void SchedulePass()
    {
        if (passScheduled) return;
        passScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            passScheduled = false;
            Passes++;
            EnsureTops();
            surface.InvalidateMeasure();
            surface.Redraw();
            UpdateLayout();
            if (Stuck) FollowEnd();
            else RestoreAnchor();
        }, DispatcherPriority.Render);
    }

    // ---- the reading band

    private void CaptureAnchor()
    {
        if (Stuck || rows.Count == 0) return;
        EnsureTops();
        var line = Offset.Y + Inset;
        var index = RowAt(line);
        if (index < 0) return;
        anchorId = rows[index].Id;
        anchorIntra = line - tops[index];
    }

    private void RestoreAnchor()
    {
        if (anchorId is null || Stuck) return;
        var index = IndexOfId(anchorId);
        if (index < 0) return;
        EnsureTops();
        var target = Math.Clamp(tops[index] + anchorIntra - Inset, 0, Math.Max(0, Extent.Height - ViewportHeight));
        if (Math.Abs(target - Offset.Y) < 0.5) return;
        programmatic = true;
        Offset = new Vector(Offset.X, target);
        Dispatcher.UIThread.Post(() => programmatic = false, DispatcherPriority.Background);
    }

    /// <summary>The reading position: the row on the reading band and how far into it the band lies, so it survives a change of heights.</summary>
    internal (string Id, double Delta)? ReadingPosition()
    {
        CaptureAnchor();
        return anchorId is null ? null : (anchorId, anchorIntra);
    }

    internal void RestoreReading((string Id, double Delta) position)
    {
        anchorId = position.Id;
        anchorIntra = position.Delta;
        Stuck = false;
        SchedulePass();
    }

    // ---- following the end

    private void Scrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta != default) { ViewportMoved?.Invoke(); surface.Redraw(); surface.InvalidateArrange(); }
        var atEnd = Offset.Y >= Extent.Height - ViewportHeight - 4;
        var was = Stuck;
        if (!programmatic && e.OffsetDelta.Y < -0.5 && !atEnd) Stuck = false;
        else if (atEnd && !(programmatic && !Stuck)) Stuck = true;
        if (was != Stuck) StuckChanged?.Invoke(Stuck);
        // Only a move of the offset says what is being read; a changed extent alone must not re-read the band, or the anchor would follow the shift.
        if (!programmatic && e.OffsetDelta != default) CaptureAnchor();
        if (Stuck && !atEnd && !programmatic) FollowEnd();
    }

    /// <summary>Scrolls to the newest item, once per frame however many items arrived.</summary>
    internal void FollowEnd() => FollowEnd(4);

    // The end is first an estimate: laying out the last rows corrects the extent, so a few passes settle it; never more.
    private void FollowEnd(int passes)
    {
        if (followScheduled) return;
        followScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            followScheduled = false;
            programmatic = true;
            ScrollToEnd();
            Dispatcher.UIThread.Post(() =>
            {
                programmatic = false;
                var atEnd = Offset.Y >= Extent.Height - ViewportHeight - 4;
                if (Stuck && !atEnd && passes > 1) FollowEnd(passes - 1);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    internal void JumpToLatest()
    {
        Stuck = true;
        StuckChanged?.Invoke(true);
        FollowEnd();
    }

    internal void ScrollToRow(ChatRow row)
    {
        var index = IndexOfId(row.Id);
        if (index < 0) return;
        Stuck = false;
        StuckChanged?.Invoke(false);
        EnsureTops();
        anchorId = row.Id;
        anchorIntra = Inset;
        programmatic = true;
        Offset = new Vector(Offset.X, Math.Clamp(tops[index] - Inset + Inset, 0, Math.Max(0, Extent.Height - ViewportHeight)));
        Dispatcher.UIThread.Post(() => programmatic = false, DispatcherPriority.Background);
        selectedId = row.Id;
        SchedulePass();
    }

    // ---- attach and detach

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        remembered = ReadingPosition();
        // A hidden tab keeps no text layouts; they are laid out again when it is shown.
        DropLayouts();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A tab that was hidden comes back where the user left it, or at the newest item when it was following.
        Dispatcher.UIThread.Post(() =>
        {
            if (Stuck) FollowEnd();
            else if (remembered is { } position) RestoreReading(position);
        }, DispatcherPriority.Render);
    }

    // ---- drawing

    private ChatRowLayout LayoutOf(ChatRow row, (string, int, int) key)
    {
        if (!layouts.TryGetValue(key, out var layout))
        {
            layout = ChatRowLayout.Build(row, width, true);
            layouts[key] = layout;
        }
        recent.Remove(key);
        recent.Add(key);
        return layout;
    }

    private void Trim(HashSet<(string, int, int)> used)
    {
        for (var i = 0; i < recent.Count && layouts.Count > used.Count + KeptLayouts;)
        {
            if (used.Contains(recent[i])) { i++; continue; }
            layouts[recent[i]].Dispose();
            layouts.Remove(recent[i]);
            recent.RemoveAt(i);
        }
    }

    private void DrawRows(DrawingContext context)
    {
        EnsureTops();
        visible.Clear();
        if (rows.Count == 0) return;
        var height = ViewportHeight;
        var from = Offset.Y - height / 4;
        var to = Offset.Y + height * 1.25;
        var used = new HashSet<(string, int, int)>();
        var changed = false;
        var w = (int)Math.Round(width);
        Frames++;
        if (Math.Abs(surface.Bounds.Width - width) >= 1) StaleFrames++;
        for (var i = Math.Max(0, RowAt(from)); i < rows.Count && tops[i] <= to; i++)
        {
            var row = rows[i];
            var key = (row.Id, row.Revision, w);
            var layout = LayoutOf(row, key);
            if (measured.Count > HeightLimit) measured.Clear();
            measured[key] = layout.Height;
            if (Math.Abs(layout.Height - current[i]) > 0.5) { current[i] = layout.Height; changed = true; }
            used.Add(key);
            visible.Add((row, i));
            using (context.PushTransform(Matrix.CreateTranslation(0, tops[i])))
            {
                layout.Draw(context, hovered == i, from - tops[i], to - tops[i], MarksFor(i, layout));
                if (hoverHit is { } over && over.RowId == row.Id) DrawHover(context, over.Hit);
                if (focusStop is { } stop && row.Id == selectedId && surface.IsFocused)
                    context.DrawRectangle(null, new Pen(Ui.Accent, 1.5), stop.Bounds.Inflate(2), 3, 3);
                if (row.Id == selectedId && surface.IsFocused)
                    context.DrawRectangle(null, new Pen(Ui.Accent, 1), new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, layout.Height - 2)), 4, 4);
            }
        }
        Trim(used);
        if (changed) { topsDirty = true; SchedulePass(); }
    }

    /// <summary>Lays out rows <paramref name="count"/> from <paramref name="first"/> without drawing them, as scrolling past them would.</summary>
    internal void MeasureRows(int first, int count)
    {
        for (var i = Math.Max(0, first); i < Math.Min(rows.Count, first + count); i++)
        {
            var key = (rows[i].Id, rows[i].Revision, (int)Math.Round(width));
            using var layout = ChatRowLayout.Build(rows[i], width, true);
            measured[key] = layout.Height;
            current[i] = layout.Height;
        }
        topsDirty = true;
        SchedulePass();
    }

    // ---- hover: the hit under the pointer gets a wash and an underline, and the pointer a hand; only the previous and the current hit are touched

    internal int HoverPaints { get; private set; }

    /// <summary>The click target under the pointer, if any: its row, kind and bounds in the row's coordinates.</summary>
    internal (string RowId, ChatHitKind Kind, Rect Bounds)? HoveredHit => hoverHit is { } over ? (over.RowId, over.Hit.Kind, over.Hit.Bounds) : null;

    internal Cursor? SurfaceCursor => surface.Cursor;

    /// <summary>The click target keyboard focus is on, if any.</summary>
    internal ChatHit? FocusedStop => focusStop;

    private void DrawHover(DrawingContext context, ChatHit hit)
    {
        HoverPaints++;
        var color = ((ISolidColorBrush)Ui.Hover).Color;
        context.DrawRectangle(new SolidColorBrush(color, 0.6), null, hit.Bounds.Inflate(2), 3, 3);
        if (hit.Kind != ChatHitKind.Toggle) context.DrawLine(new Pen(Ui.Accent, 1), new Point(hit.Bounds.Left, hit.Bounds.Bottom), new Point(hit.Bounds.Right, hit.Bounds.Bottom));
    }

    private void TrackHover(Point point, int index)
    {
        (string, ChatHit)? next = null;
        if (index >= 0 && point.Y <= tops[^1])
        {
            var row = rows[index];
            var layout = LayoutOf(row, (row.Id, row.Revision, (int)Math.Round(width)));
            var local = new Point(point.X, point.Y - tops[index]);
            if (layout.HitAt(local) is { } hit && !(hit.Kind is ChatHitKind.Rewind or ChatHitKind.Fork && hovered != index))
                next = (row.Id, hit);
            else if (row.Item is ChatToolItem or ChatThinkingItem or ChatActivityItem && local.Y < 24) next = (row.Id, new ChatHit(new Rect(0, 0, width, 22), ChatHitKind.Toggle));
        }
        if (next?.Item1 == hoverHit?.RowId && next?.Item2 == hoverHit?.Hit) return;
        hoverHit = next;
        surface.Cursor = next is null ? null : Hand;
        surface.Redraw();
    }

    // ---- input

    private void Pressed(PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(surface).Properties;
        if (properties.IsRightButtonPressed) { OpenMenu(e.GetPosition(surface)); return; }
        if (!properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(surface);
        var index = RowAt(point.Y);
        if (index < 0 || point.Y > tops[^1]) { ClearSelection(); return; }
        var row = rows[index];
        selectedId = row.Id;
        var layout = LayoutOf(row, (row.Id, row.Revision, (int)Math.Round(width)));
        var local = new Point(point.X, point.Y - tops[index]);
        var hovering = hovered == index;
        pendingLink = null;
        if (layout.HitAt(local) is { } hit && !(hit.Kind is ChatHitKind.Rewind or ChatHitKind.Fork && !hovering))
        {
            if (hit.Kind == ChatHitKind.Link) pendingLink = (row, hit.Arg);
            else
            {
                actions.Activate(row, hit.Kind, hit.Arg);
                e.Handled = true;
                surface.Focus();
                surface.Redraw();
                return;
            }
        }
        else if (row.Item is ChatToolItem or ChatThinkingItem or ChatActivityItem && local.Y < 24)
        {
            actions.Activate(row, ChatHitKind.Toggle);
            e.Handled = true;
            surface.Focus();
            surface.Redraw();
            return;
        }
        surface.Focus();
        BeginSelection(e, row, layout, local);
        surface.Redraw();
    }

    // ---- text selection: anchor and focus are a row id and a character offset in the row's selectable text, so they outlive virtualization, streaming and height changes

    private bool HasSelection => anchorAt is { } a && focusAt is { } f && (a.Id != f.Id || a.Offset != f.Offset);

    private void BeginSelection(PointerPressedEventArgs e, ChatRow row, ChatRowLayout layout, Point local)
    {
        layout.Realize(local.Y - 1, local.Y + 1);
        var offset = layout.OffsetAt(local);
        if (offset < 0) { ClearSelection(); return; }
        var text = layout.SelectableText();
        pressedAt = e.GetPosition(surface);
        dragged = false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && anchorAt is not null) focusAt = (row.Id, offset);
        else if (e.ClickCount == 2)
        {
            var (start, end) = ChatRowLayout.WordAt(text, offset);
            anchorAt = (row.Id, start); focusAt = (row.Id, end);
        }
        else if (e.ClickCount >= 3)
        {
            var (start, end) = ChatRowLayout.ParagraphAt(text, offset);
            anchorAt = (row.Id, start); focusAt = (row.Id, end);
        }
        else { anchorAt = focusAt = (row.Id, offset); }
        selecting = true;
        e.Pointer.Capture(surface);
    }

    private void ClearSelection()
    {
        if (anchorAt is null && focusAt is null) return;
        anchorAt = focusAt = null;
        surface.Redraw();
    }

    /// <summary>The selection ordered by position, or null when nothing is selected.</summary>
    private ((int Row, int Offset) From, (int Row, int Offset) To)? Ordered()
    {
        if (!HasSelection) return null;
        var a = IndexOfId(anchorAt!.Value.Id);
        var f = IndexOfId(focusAt!.Value.Id);
        if (a < 0 || f < 0) return null;
        var first = (a, anchorAt.Value.Offset);
        var second = (f, focusAt.Value.Offset);
        return first.CompareTo(second) <= 0 ? (first, second) : (second, first);
    }

    private List<Rect>? MarksFor(int index, ChatRowLayout layout)
    {
        if (Ordered() is not { } range || index < range.From.Row || index > range.To.Row) return null;
        var from = index == range.From.Row ? range.From.Offset : 0;
        var to = index == range.To.Row ? range.To.Offset : int.MaxValue;
        var marks = layout.RangeRects(from, to);
        return marks.Count == 0 ? null : marks;
    }

    private void Dragged(PointerEventArgs e)
    {
        if (!selecting) return;
        var point = e.GetPosition(surface);
        if (!dragged && Math.Abs(point.X - pressedAt.X) + Math.Abs(point.Y - pressedAt.Y) < 4) return;
        dragged = true;
        pendingLink = null;
        var inViewport = e.GetPosition(this);
        pointerX = point.X;
        pointerY = inViewport.Y;
        MoveFocus();
        var near = pointerY < 24 || pointerY > ViewportHeight - 24;
        if (near && edgeTimer is null)
        {
            edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            edgeTimer.Tick += (_, _) => EdgeScroll();
            edgeTimer.Start();
        }
        else if (!near) StopEdgeTimer();
    }

    private void EdgeScroll()
    {
        if (!selecting) { StopEdgeTimer(); return; }
        var step = pointerY < 24 ? -(24 - Math.Max(pointerY, -24)) : pointerY - (ViewportHeight - 24);
        Offset = new Vector(Offset.X, Math.Clamp(Offset.Y + step, 0, Math.Max(0, Extent.Height - ViewportHeight)));
        MoveFocus();
    }

    private void StopEdgeTimer()
    {
        edgeTimer?.Stop();
        edgeTimer = null;
    }

    private void MoveFocus()
    {
        EnsureTops();
        if (rows.Count == 0) return;
        var y = Math.Clamp(Offset.Y + pointerY, 0, Math.Max(0, tops[^1] - 1));
        var index = RowAt(y);
        if (index < 0) return;
        var row = rows[index];
        var layout = LayoutOf(row, (row.Id, row.Revision, (int)Math.Round(width)));
        var local = new Point(pointerX, y - tops[index]);
        layout.Realize(local.Y - 1, local.Y + 1);
        var offset = layout.OffsetAt(local);
        if (offset < 0) return;
        focusAt = (row.Id, offset);
        surface.Redraw();
    }

    private void Released(PointerReleasedEventArgs e)
    {
        if (!selecting) return;
        selecting = false;
        StopEdgeTimer();
        e.Pointer.Capture(null);
        if (!dragged && pendingLink is { } link) actions.Activate(link.Row, ChatHitKind.Link, link.Arg);
        pendingLink = null;
        // A plain click selects nothing: the caret-less anchor is dropped.
        if (!HasSelection) { anchorAt = focusAt = null; surface.Redraw(); }
    }

    internal void SelectAll()
    {
        if (rows.Count == 0) return;
        anchorAt = (rows[0].Id, 0);
        focusAt = (rows[^1].Id, int.MaxValue);
        surface.Redraw();
    }

    internal bool Selecting => HasSelection;

    internal ((string Id, int Offset) Anchor, (string Id, int Offset) Focus)? SelectionRange => HasSelection ? (anchorAt!.Value, focusAt!.Value) : null;

    /// <summary>Sets the selection directly, as a drag or a word select would.</summary>
    internal void Select((string Id, int Offset) from, (string Id, int Offset) to)
    {
        anchorAt = from;
        focusAt = to;
        surface.Redraw();
    }

    private string TextOfRow(ChatRow row)
    {
        var key = (row.Id, row.Revision);
        if (rowTexts.TryGetValue(key, out var known)) return known;
        string text;
        var width = (int)Math.Round(this.width);
        if (layouts.TryGetValue((row.Id, row.Revision, width), out var layout)) text = layout.SelectableText();
        else
        {
            // A row that is not near the viewport is laid out only to read its text, and let go at once.
            using var transient = ChatRowLayout.Build(row, this.width, true);
            text = transient.SelectableText();
        }
        if (rowTexts.Count > 60_000) rowTexts.Clear();
        rowTexts[key] = text;
        return text;
    }

    /// <summary>The selected text, rows separated by a blank line. Rows far from the viewport are read in short slices, so the UI thread never waits on a long transcript.</summary>
    internal async Task<string> SelectedTextAsync()
    {
        if (Ordered() is not { } range) return "";
        var builder = new System.Text.StringBuilder();
        for (var i = range.From.Row; i <= range.To.Row && i < rows.Count; i++)
        {
            var text = TextOfRow(rows[i]);
            var from = i == range.From.Row ? Math.Min(range.From.Offset, text.Length) : 0;
            var to = i == range.To.Row ? Math.Min(range.To.Offset, text.Length) : text.Length;
            if (to > from)
            {
                if (builder.Length > 0) builder.Append("\n\n");
                builder.Append(text, from, to - from);
            }
            if (i % 100 == 99) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        return builder.ToString();
    }

    internal async Task CopySelectionAsync()
    {
        var text = await SelectedTextAsync();
        if (text.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    // What Copy takes from a message with nothing selected: a reply's markdown, a prompt's text.
    internal static string? CopyText(ChatItem item) => item switch
    {
        ChatAssistantItem { Text.Length: > 0 } reply => reply.Text,
        ChatUserItem { Text.Length: > 0 } prompt => prompt.Text,
        _ => null
    };

    // Right-click copies the selection when there is one, else the message under the pointer.
    private void OpenMenu(Point point)
    {
        var index = RowAt(point.Y);
        var message = HasSelection || index < 0 || point.Y > tops[^1] ? null : CopyText(rows[index].Item);
        if (!HasSelection && message is null) return;
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += async (_, _) =>
        {
            if (message is null) { await CopySelectionAsync(); return; }
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, message);
        };
        new ContextMenu { ItemsSource = new[] { copy } }.Open(surface);
    }

    private void Moved(PointerEventArgs e)
    {
        Dragged(e);
        var position = e.GetPosition(surface);
        var index = RowAt(position.Y);
        TrackHover(position, index);
        if (index == hovered) return;
        hovered = index;
        surface.Redraw();
    }

    private void Left()
    {
        if (hoverHit is not null) { hoverHit = null; surface.Cursor = null; surface.Redraw(); }
        if (hovered < 0) return;
        hovered = -1;
        surface.Redraw();
    }

    private void Key(KeyEventArgs e)
    {
        if (rows.Count == 0) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Avalonia.Input.Key.A) { SelectAll(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.C && HasSelection) { _ = CopySelectionAsync(); e.Handled = true; }
            return;
        }
        var index = selectedId is null ? -1 : IndexOfId(selectedId);
        if (e.Key == Avalonia.Input.Key.Tab && index >= 0 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var row = rows[index];
            var stops = LayoutOf(row, (row.Id, row.Revision, (int)Math.Round(width))).TabStops(hovered == index);
            var at = focusStop is { } current ? stops.ToList().FindIndex(stop => stop == current) : -1;
            var next = e.KeyModifiers == KeyModifiers.Shift ? (at < 0 ? stops.Count - 1 : at - 1) : at + 1;
            if (next >= 0 && next < stops.Count) { focusStop = stops[next]; surface.Redraw(); e.Handled = true; return; }
            focusStop = null;
            surface.Redraw();
            return;
        }
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space && focusStop is { } focused && index >= 0)
        {
            actions.Activate(rows[index], focused.Kind, focused.Arg);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Avalonia.Input.Key.Down: Select(Math.Min(rows.Count - 1, index + 1)); break;
            case Avalonia.Input.Key.Up: Select(Math.Max(0, index < 0 ? rows.Count - 1 : index - 1)); break;
            case Avalonia.Input.Key.Home: Select(0); break;
            case Avalonia.Input.Key.End: JumpToLatest(); selectedId = rows[^1].Id; surface.Redraw(); break;
            case Avalonia.Input.Key.PageDown: Offset = new Vector(Offset.X, Offset.Y + ViewportHeight * 0.9); break;
            case Avalonia.Input.Key.PageUp: Offset = new Vector(Offset.X, Math.Max(0, Offset.Y - ViewportHeight * 0.9)); break;
            case Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space when index >= 0: actions.Activate(rows[index], ChatHitKind.Toggle); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Select(int index)
    {
        if (index < 0 || index >= rows.Count) return;
        focusStop = null;
        selectedId = rows[index].Id;
        EnsureTops();
        // Keep the selected row inside the viewport.
        if (tops[index] < Offset.Y) Offset = new Vector(Offset.X, tops[index]);
        else if (tops[index + 1] > Offset.Y + ViewportHeight) Offset = new Vector(Offset.X, Math.Max(0, tops[index + 1] - ViewportHeight));
        surface.Redraw();
        surface.InvalidateArrange();
    }

    internal void Activate(ChatRow row, ChatHitKind kind, string? arg = null) => actions.Activate(row, kind, arg);

    // ---- the surface and its accessibility stubs

    private sealed class Surface : Panel
    {
        private readonly ChatList owner;
        private readonly List<RowStub> stubs = [];

        private readonly Drawing drawing;

        internal Surface(ChatList owner)
        {
            this.owner = owner;
            drawing = new Drawing(owner) { IsHitTestVisible = false };
            Children.Add(drawing);
            Focusable = true;
            ClipToBounds = false;
            Background = Brushes.Transparent;
            PointerPressed += (_, e) => owner.Pressed(e);
            PointerMoved += (_, e) => owner.Moved(e);
            PointerReleased += (_, e) => owner.Released(e);
            PointerExited += (_, _) => owner.Left();
            KeyDown += (_, e) => owner.Key(e);
            GotFocus += (_, _) => Redraw();
            LostFocus += (_, _) => Redraw();
            Ui.ThemeChanged += Retheme;
        }

        internal void Redraw() => drawing.InvalidateVisual();

        private void Retheme() { owner.DropLayouts(); Redraw(); }

        protected override Size MeasureOverride(Size availableSize)
        {
            var w = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
            if (Math.Abs(w - owner.width) >= 1)
            {
                owner.CaptureAnchor();
                owner.width = w;
                // Layouts are keyed by width, so the old ones simply stop matching and are trimmed after the next draw: nothing is dropped
                // here, and the rows in view are laid out now, once, so the first frame at the new width already has their true heights.
                owner.Reset();
                owner.Prelayout();
                owner.SchedulePass();
            }
            owner.EnsureTops();
            return new Size(w, owner.tops[^1]);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            owner.EnsureTops();
            drawing.Arrange(new Rect(finalSize));
            // One stub per row in range, so assistive technology sees a list of items without any row owning a control.
            var height = owner.ViewportHeight;
            var first = Math.Max(0, owner.RowAt(owner.Offset.Y - height / 4));
            var count = 0;
            for (var i = first; i < owner.rows.Count && owner.tops[i] <= owner.Offset.Y + height * 1.25; i++, count++)
            {
                if (count >= stubs.Count) { var stub = new RowStub(); stubs.Add(stub); Children.Add(stub); }
                var item = stubs[count];
                AutomationProperties.SetName(item, Describe(owner.rows[i].Item));
                item.Arrange(new Rect(0, owner.tops[i], finalSize.Width, Math.Max(1, owner.current[i])));
            }
            while (stubs.Count > count) { Children.Remove(stubs[^1]); stubs.RemoveAt(stubs.Count - 1); }
            return finalSize;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ListPeer(this);
    }

    internal void DropLayouts()
    {
        foreach (var layout in layouts.Values) layout.Dispose();
        layouts.Clear();
        recent.Clear();
    }

    // The layer that draws the visible rows; the surface around it is a panel only so the stubs can be its children.
    private sealed class Drawing(ChatList owner) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            owner.DrawRows(context);
        }
    }

    private sealed class ListPeer(Control owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string? GetNameCore() => "Conversation";
    }

    private sealed class RowStub : Control
    {
        internal RowStub() { IsHitTestVisible = false; Focusable = false; }
        protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this);
    }

    private sealed class ItemPeer(Control owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    }
}