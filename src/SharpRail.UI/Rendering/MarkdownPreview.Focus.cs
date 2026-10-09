using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Markdig.Syntax;

namespace SharpRail.UI.Rendering;

/// <summary>
/// What a rendered diff shows: which top-level blocks of the merged document changed, which items of its changed
/// lists changed, and the hidden runs the reader has expanded. Computed off the UI thread.
/// </summary>
public sealed record DiffFocus(IReadOnlyList<bool> Blocks, IReadOnlyDictionary<int, bool[]> Items, ISet<string> Expanded, Action? ExpandedChanged = null)
{
    /// <summary>Unchanged units kept visible on each side of a change.</summary>
    public const int Context = 2;

    /// <summary>
    /// A unit changed when the alignment of the two documents' units finds it no counterpart. The alignment is
    /// positional, so an identical block elsewhere cannot vouch for it, and it compares source text, so a change that
    /// earns no mark (a ticked checkbox, a list's start, a code fence) still counts.
    /// </summary>
    public static DiffFocus Create(string before, string merged, MarkdownDocument document, ISet<string> expanded, Action? expandedChanged = null, CancellationToken token = default)
    {
        var original = MarkdownPreview.Parse(before);
        var blocks = Unmatched(Sources(original, before), Sources(document, merged), token);
        var lists = original.OfType<ListBlock>().ToArray();
        var pairs = document.OfType<ListBlock>().Count() == lists.Length;
        var items = new Dictionary<int, bool[]>();
        var ordinal = 0;
        for (var index = 0; index < document.Count; index++)
        {
            if (document[index] is not ListBlock list) continue;
            var twin = pairs ? lists[ordinal] : null;
            ordinal++;
            if (!blocks[index]) continue;
            var sources = Sources(list, merged);
            var changed = twin is null
                ? [.. sources.Select(source => source.Contains("<ins>", StringComparison.Ordinal) || source.Contains("<del>", StringComparison.Ordinal))]
                : Unmatched(Sources(twin, before), sources, token);
            // A list that differs only in its own attributes renders whole.
            if (changed.Any(flag => flag)) items[index] = changed;
        }
        return new(blocks, items, expanded, expandedChanged);
    }

    private static string[] Sources(ContainerBlock container, string text) =>
        [.. container.Select(block => block.Span.Start >= 0 && block.Span.End < text.Length ? text.Substring(block.Span.Start, block.Span.Length).Trim() : "")];

    private static bool[] Unmatched(string[] before, string[] after, CancellationToken token)
    {
        var changed = new bool[after.Length];
        var index = 0;
        foreach (var (side, _) in MarkdownDiff.Sequence(before, after, token))
        {
            if (side < 0) continue;
            changed[index++] = side > 0;
        }
        return changed;
    }

    /// <summary>
    /// Runs of units as shown or hidden, with Git's hunk rule: context stays on each side of a change, and a run of
    /// one is never hidden, since an expander for one unit saves nothing. With no change everything is one hidden run.
    /// </summary>
    public static List<(int Start, int Count, bool Hidden)> Segments(IReadOnlyList<bool> changed)
    {
        var visible = new bool[changed.Count];
        for (var index = 0; index < changed.Count; index++)
            if (changed[index])
                for (var near = Math.Max(0, index - Context); near <= Math.Min(changed.Count - 1, index + Context); near++) visible[near] = true;
        var any = changed.Any(flag => flag);
        var segments = new List<(int, int, bool)>();
        for (var start = 0; start < visible.Length;)
        {
            var end = start;
            while (end < visible.Length && visible[end] == visible[start]) end++;
            var hidden = !visible[start] && (end - start > 1 || !any);
            if (segments.Count > 0 && segments[^1] is var (previousStart, previousCount, previousHidden) && previousHidden == hidden)
                segments[^1] = (previousStart, previousCount + end - start, hidden);
            else segments.Add((start, end - start, hidden));
            start = end;
        }
        return segments;
    }
}

public sealed partial class MarkdownPreview
{
    private void RenderFocused(StackPanel body, DiffFocus focus)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        if (!focus.Blocks.Any(changed => changed))
        {
            var notice = Ui.Text("The rendered document did not change — the difference is in the source. Use the Source view.", Ui.Muted, 12);
            notice.Name = "RenderedDiffEmpty"; notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(notice);
        }
        foreach (var (start, count, hidden) in DiffFocus.Segments(focus.Blocks))
        {
            IEnumerable<Control> Shown() => Enumerable.Range(start, count).Select(index =>
                Document[index] is ListBlock list && focus.Items.TryGetValue(index, out var items) ? FocusedList(list, items, index, focus, live) : Render(Document[index]));
            if (!hidden) { body.Children.AddRange(Shown()); continue; }
            var heading = Enumerable.Range(start, count).Select(index => Document[index]).OfType<HeadingBlock>().LastOrDefault();
            var label = $"{count} unchanged {(count == 1 ? "block" : "blocks")}" + (heading?.Inline is { } title ? " · " + Plain(title) : "");
            body.Children.Add(Collapsed("b" + start, label, focus, live, Shown));
        }
        // An expansion survives a refresh only while its run still starts at the same position.
        if (focus.Expanded.Count > 0 && !focus.Expanded.IsSubsetOf(live))
        {
            focus.Expanded.IntersectWith(live);
            focus.ExpandedChanged?.Invoke();
        }
    }

    private Control Collapsed(string key, string label, DiffFocus focus, HashSet<string> live, Func<IEnumerable<Control>> content)
    {
        live.Add(key);
        Control Expanded()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12) };
            panel.Children.AddRange(content());
            CollapseMargins(panel);
            return panel;
        }
        if (focus.Expanded.Contains(key)) return Expanded();
        var button = new Button
        {
            Name = "RenderedDiffCollapsed",
            Content = Ui.Text("⋯ " + label, Ui.Hint, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 8),
            Background = Ui.Elevated,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        button.Classes.Add("rendered-diff-collapsed");
        // Expansion is one-way: Source and the file preview already offer the whole document.
        button.Click += (_, _) =>
        {
            if (button.Parent is not Panel parent) return;
            var index = parent.Children.IndexOf(button);
            parent.Children[index] = Expanded();
            focus.Expanded.Add(key);
            focus.ExpandedChanged?.Invoke();
        };
        return button;
    }

    private StackPanel FocusedList(ListBlock list, bool[] changed, int unit, DiffFocus focus, HashSet<string> live)
    {
        var rows = ListRows(list);
        var items = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12) };
        foreach (var (start, count, hidden) in DiffFocus.Segments(changed))
        {
            // Rows are numbered before any is hidden, so hiding items never renumbers the rest.
            IEnumerable<Control> Shown() => Enumerable.Range(start, count).Where(index => index < rows.Count).Select(index => rows[index]());
            if (hidden) items.Children.Add(Collapsed($"l{unit}:{start}", $"{count} unchanged {(count == 1 ? "item" : "items")}", focus, live, Shown));
            else items.Children.AddRange(Shown());
        }
        return items;
    }
}