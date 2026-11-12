using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.BranchGraph.UI;

internal sealed class GraphLabels : Control
{
    private GitGraphCommit? commit;
    private IReadOnlyList<string> worktrees = [];
    private readonly List<(TextLayout Text, Point At, Rect? Badge, string? Ref)> layouts = [];
    private double layoutWidth = -1;

    internal void Update(GitGraphCommit next, IReadOnlyList<string> marks)
    {
        if (commit == next && worktrees.SequenceEqual(marks)) return;
        commit = next;
        worktrees = marks;
        Clear();
        InvalidateVisual();
    }

    private void Clear()
    {
        foreach (var (text, _, _, _) in layouts) text.Dispose();
        layouts.Clear();
        layoutWidth = -1;
    }

    private void Retheme()
    {
        Clear();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Ui.ThemeChanged += Retheme;
        Retheme();
    }

    /// <summary>
    /// The branch label under a point whose name is among the commit's local branches; null over anything else, tags and remote-tracking
    /// labels included.
    /// </summary>
    internal string? LocalBranchAt(Point point)
    {
        Build();
        if (commit?.Branches is not { } branches) return null;
        foreach (var (_, _, badge, reference) in layouts)
            if (reference is not null && badge is { } rectangle && rectangle.Contains(point)) return branches.Contains(reference) ? reference : null;
        return null;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, LaneArt.RowHeight);

    public override void Render(DrawingContext context)
    {
        Build();
        using (context.PushClip(new Rect(Bounds.Size)))
            foreach (var (text, at, badge, _) in layouts)
            {
                if (badge is { } rectangle) context.DrawRectangle(Ui.PrimarySubtle, null, rectangle, 3, 3);
                text.Draw(context, at);
            }
    }

    // The text layouts at the current width, built on first use so a hit test needs no earlier paint.
    private void Build()
    {
        if (commit is null || Bounds.Width <= 0) return;
        if (layoutWidth != Bounds.Width)
        {
            Clear();
            layoutWidth = Bounds.Width;
            var face = new Typeface(Ui.InterfaceFont, FontStyle.Normal, Ui.InterfaceWeight);
            var subject = new TextLayout(commit.Subject, face, Ui.FontSize, Ui.TextBrush,
                textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Bounds.Width, maxLines: 1);
            var metadataHeight = 14d;
            var top = Math.Max(0, (Bounds.Height - subject.Height - metadataHeight) / 2);
            layouts.Add((subject, new(0, top), null, null));
            var x = 0d;
            var y = top + subject.Height;
            void Label(string value, IBrush color, double maximum, bool badge = false, bool reference = false)
            {
                if (x >= Bounds.Width) return;
                var available = Math.Min(maximum, Bounds.Width - x);
                if (available <= (badge ? 8 : 0)) return;
                var text = new TextLayout(value, face, 11, color, textTrimming: TextTrimming.CharacterEllipsis,
                    maxWidth: available - (badge ? 8 : 0), maxLines: 1);
                var width = text.Width + (badge ? 8 : 0);
                layouts.Add((text, new(x + (badge ? 4 : 0), y), badge ? new Rect(x, y, width, metadataHeight) : null, reference ? value : null));
                x += width + 4;
            }
            Label(commit.ShortSha, Ui.Hint, Bounds.Width);
            Label(commit.Author, Ui.Hint, 120);
            foreach (var reference in commit.Refs) Label(reference, Ui.Accent, 168, badge: true, reference: true);
            foreach (var worktree in worktrees) Label("⌂ " + worktree, Ui.Muted, 140);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Ui.ThemeChanged -= Retheme;
        Clear();
        base.OnDetachedFromVisualTree(e);
    }
}