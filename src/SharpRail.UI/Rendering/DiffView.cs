using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Scintilla;
using SharpRail.UI.Editor;

namespace SharpRail.UI.Rendering;

internal sealed partial class DiffView : Grid, IDisposable
{
    private sealed record Hunk(int OldStart, int OldCount, int NewStart, string Header, List<string> Lines);

    private readonly Grid body = new();
    private readonly ToggleButton split = Toggle("DiffSplit", "layout", "Side-by-side diff");
    private readonly ToggleButton inline = Toggle("DiffInline", "list", "Inline diff");
    private readonly ToggleButton whitespace = Toggle("DiffWhitespace", "collapseVertical", "Hide whitespace changes");
    private readonly ToggleButton source = Segment("DiffSource", "Source");
    private readonly ToggleButton rendered = Segment("DiffRendered", "Rendered");
    private readonly double wrapWidth;
    private readonly Func<CancellationToken, Task<Control?>>? renderMerged;
    private readonly Action<bool>? renderedChanged;
    private CancellationTokenSource? merge;
    private Control? merged;
    private readonly List<EditorFrame> frames = [];
    private bool disposed;
    private string text;

    /// <summary>
    /// A unified diff viewer. Markdown diffs pass <paramref name="renderMerged"/> to offer the reference's
    /// Source|Rendered toggle; their source view is always side by side. A null rendering means the documents
    /// exceed <see cref="ViewerLimits.RenderedMarkdown"/>, and the view falls back to source.
    /// </summary>
    internal DiffView(string text, string path, double wrapWidth,
        Func<CancellationToken, Task<Control?>>? renderMerged = null, bool showRendered = false, Action<bool>? renderedChanged = null)
    {
        this.text = text;
        this.wrapWidth = wrapWidth;
        this.renderMerged = renderMerged;
        this.renderedChanged = renderedChanged;
        Name = "DiffPane";
        RowDefinitions = new RowDefinitions("32,*");
        var chip = new Border
        {
            Name = "DiffPath",
            Background = Ui.Elevated,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text(path, Ui.TextBrush, 12)
        };
        var copy = new Button
        {
            Name = "DiffCopy",
            Content = Ui.Icon("file", size: 14),
            Width = 28,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetName(copy, "Copy diff");
        ToolTip.SetTip(copy, "Copy diff");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(this.text);
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(whitespace); controls.Children.Add(copy);
        // Scintilla is macOS-only; elsewhere the raw diff is shown without view modes.
        if (renderMerged is null && OperatingSystem.IsMacOS()) { controls.Children.Insert(0, inline); controls.Children.Insert(0, split); }
        else { controls.Children.Add(source); controls.Children.Add(rendered); }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        Ui.Place(header, chip); Ui.Place(header, controls, 0, 1);
        Ui.Place(this, header);
        Ui.Place(this, body, 1);
        split.IsChecked = true; inline.IsChecked = false;
        split.Click += (_, _) => { split.IsChecked = true; inline.IsChecked = false; Render(); };
        inline.Click += (_, _) => { inline.IsChecked = true; split.IsChecked = false; Render(); };
        whitespace.Click += (_, _) => Render();
        IsRendered = showRendered && renderMerged is not null;
        source.IsChecked = !IsRendered; rendered.IsChecked = IsRendered;
        source.Click += (_, _) => ShowRendered(false);
        rendered.Click += (_, _) => ShowRendered(true);
        Render();
    }

    internal bool IsRendered { get; private set; }

    internal void Update(string diff)
    {
        if (diff == text) return;
        text = diff;
        if (IsRendered) StartMerge(keepCurrent: true);
        else Render();
    }

    private void ShowRendered(bool show)
    {
        source.IsChecked = !show; rendered.IsChecked = show;
        if (show == IsRendered) return;
        IsRendered = show;
        renderedChanged?.Invoke(show);
        Render();
    }

    private static ToggleButton Segment(string name, string label)
    {
        var button = new ToggleButton
        {
            Name = name,
            Content = label,
            Height = 20,
            Padding = new Thickness(8, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Foreground = Ui.Muted
        };
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
        {
            button.Resources["ToggleButtonBackground" + state] = Ui.Hover;
            button.Resources["ToggleButtonForeground" + state] = Ui.TextBrush;
        }
        AutomationProperties.SetName(button, label);
        return button;
    }

    // Merges run off the UI thread; a newer merge or leaving the rendered view cancels the stale one.
    private void StartMerge(bool keepCurrent)
    {
        merge?.Cancel(); merge?.Dispose();
        var cancellation = merge = new CancellationTokenSource();
        if (!keepCurrent || merged is null) ShowBody(Placeholder("RenderedDiffLoading", "Rendering diff…", Ui.Muted));
        _ = Complete();

        async Task Complete()
        {
            Control? result;
            try { result = await renderMerged!(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (cancellation.IsCancellationRequested || disposed) return;
                Console.Error.WriteLine(error);
                result = Placeholder("RenderedDiffError", "Rendered diff failed — use the Source view.", Ui.Danger);
            }
            if (cancellation.IsCancellationRequested || disposed) { (result as IDisposable)?.Dispose(); return; }
            if (ReferenceEquals(merge, cancellation)) { merge = null; cancellation.Dispose(); }
            if (result is null) { RenderedUnavailable(); return; }
            ShowBody(result);
            merged = result;
        }
    }

    // Leaves the tab's Rendered preference alone, so reopening it after the document shrinks renders again.
    private void RenderedUnavailable()
    {
        rendered.IsEnabled = false;
        ToolTip.SetTip(rendered, "Too large to render — showing the source diff");
        IsRendered = false;
        source.IsChecked = true; rendered.IsChecked = false;
        Render();
    }

    private void ShowBody(Control content)
    {
        DropMerged();
        Ui.Place(body, content);
    }

    private void DropMerged()
    {
        body.Children.Clear();
        foreach (var frame in frames) frame.Editor.Dispose();
        frames.Clear();
        (merged as IDisposable)?.Dispose();
        merged = null;
    }

    private static Control Placeholder(string name, string message, IBrush color)
    {
        var text = Ui.Text(message, color, 12);
        text.Name = name;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        merge?.Cancel(); merge?.Dispose(); merge = null;
        DropMerged();
    }

    private static ToggleButton Toggle(string name, string icon, string tooltip)
    {
        var button = new ToggleButton
        {
            Name = name,
            Content = Ui.Icon(icon, size: 14),
            Width = 28,
            Height = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent
        };
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
            button.Resources["ToggleButtonBackground" + state] = Ui.Hover;
        AutomationProperties.SetName(button, tooltip);
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private void Render()
    {
        whitespace.IsVisible = !IsRendered && OperatingSystem.IsMacOS();
        if (IsRendered) { StartMerge(keepCurrent: false); return; }
        merge?.Cancel(); merge?.Dispose(); merge = null;
        DropMerged();
        if (text.Length > ViewerLimits.Scintilla) { Ui.Place(body, ViewerLimits.TooLarge("diff", text.Length)); return; }
        var (preamble, hunks) = Parse(text);
        if (whitespace.IsChecked == true) foreach (var hunk in hunks) IgnoreWhitespace(hunk);
        if (hunks.Count == 0 && string.IsNullOrWhiteSpace(text))
        {
            var empty = Ui.Text("No changes", Ui.Hint, 12);
            empty.Name = "DiffEmpty"; empty.Margin = new Thickness(20); empty.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Place(body, empty); return;
        }
        if (!OperatingSystem.IsMacOS())
        {
            Ui.Place(body, new ScrollViewer { Content = MarkdownPreview.Code(text), Margin = new Thickness(20, 8), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            return;
        }
        if (hunks.Count == 0 || inline.IsChecked == true) RenderInline(preamble, hunks);
        else RenderSplit(hunks);
    }

    private enum LineKind { Context, Added, Removed, Header, Meta, Gap, Filler }

    private sealed class Side
    {
        internal readonly StringBuilder Text = new();
        internal readonly List<int> Styles = [];
        // The old and new file line numbers each row shows in its gutter.
        internal readonly List<(int? Old, int? New)> Numbers = [];

        internal void Add(string line, LineKind kind, int? old = null, int? @new = null)
        {
            if (Styles.Count > 0) Text.Append('\n');
            Text.Append(line);
            Styles.Add((int)kind);
            Numbers.Add((old, @new));
        }

        internal void AddGap(int hidden) { if (hidden > 0) Add($"⋯ {hidden} hidden lines", LineKind.Gap); }

        internal string?[] Labels(bool old, bool @new)
        {
            var width = Numbers.Max(pair => Math.Max(pair.Old ?? 0, pair.New ?? 0)).ToString(CultureInfo.InvariantCulture).Length;
            string Column(int? number) => (number?.ToString(CultureInfo.InvariantCulture) ?? "").PadLeft(width);
            return Numbers.Select(pair => pair is (null, null) ? null
                : old && @new ? Column(pair.Old) + " " + Column(pair.New)
                : Column(old ? pair.Old : pair.New)).ToArray();
        }
    }

    // Indexed by LineKind; Scintilla needs opaque colours, so the washes are composited over the surface.
    private static ScintillaLineStyle[] LineStyles() =>
    [
        new(Ui.Muted.Color),
        new(Ui.Success.Color, Ui.Over(Ui.SuccessWash.Color, Ui.Surface.Color)),
        new(Ui.Danger.Color, Ui.Over(Ui.DangerWash.Color, Ui.Surface.Color)),
        new(Ui.Accent.Color),
        new(Ui.Muted.Color),
        new(Ui.Hint.Color),
        new(Ui.Muted.Color)
    ];

    private void RenderInline(List<string> preamble, List<Hunk> hunks)
    {
        var side = new Side();
        foreach (var line in preamble) side.Add(line, LineKind.Meta);
        var previous = (Hunk?)null;
        foreach (var hunk in hunks)
        {
            side.AddGap(Gap(previous, hunk));
            side.Add(hunk.Header, LineKind.Header);
            int old = hunk.OldStart, @new = hunk.NewStart;
            foreach (var line in hunk.Lines)
            {
                if (line.StartsWith('+')) side.Add(line, LineKind.Added, null, @new++);
                else if (line.StartsWith('-')) side.Add(line, LineKind.Removed, old++);
                else if (line.StartsWith('\\')) side.Add(line, LineKind.Meta);
                else side.Add(line, LineKind.Context, old++, @new++);
            }
            previous = hunk;
        }
        Ui.Place(body, Code("DiffInlineText", side, side.Labels(true, true)));
    }

    private void RenderSplit(List<Hunk> hunks)
    {
        var oldSide = new Side();
        var newSide = new Side();
        var previous = (Hunk?)null;
        foreach (var hunk in hunks)
        {
            var gap = Gap(previous, hunk);
            oldSide.AddGap(gap); newSide.AddGap(gap);
            int old = hunk.OldStart, @new = hunk.NewStart;
            var removed = new List<string>(); var added = new List<string>();
            void Flush()
            {
                for (var index = 0; index < Math.Max(removed.Count, added.Count); index++)
                {
                    if (index < removed.Count) oldSide.Add(removed[index], LineKind.Removed, old++); else oldSide.Add("", LineKind.Filler);
                    if (index < added.Count) newSide.Add(added[index], LineKind.Added, null, @new++); else newSide.Add("", LineKind.Filler);
                }
                removed.Clear(); added.Clear();
            }
            foreach (var line in hunk.Lines)
            {
                if (line.StartsWith('-')) removed.Add(line[1..]);
                else if (line.StartsWith('+')) added.Add(line[1..]);
                else if (line.StartsWith('\\')) continue;
                else
                {
                    Flush();
                    var content = line.Length > 0 ? line[1..] : "";
                    oldSide.Add(content, LineKind.Context, old++);
                    newSide.Add(content, LineKind.Context, null, @new++);
                }
            }
            Flush();
            previous = hunk;
        }
        var oldFrame = Code("DiffOldText", oldSide, oldSide.Labels(true, false));
        var newFrame = Code("DiffNewText", newSide, newSide.Labels(false, true));
        // Both sides hold the same number of document lines.
        Follow(oldFrame.Editor, newFrame.Editor); Follow(newFrame.Editor, oldFrame.Editor);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        Ui.Place(columns, oldFrame);
        Ui.Place(columns, new Border { Width = 1, Background = Ui.BorderBrush }, 0, 1);
        Ui.Place(columns, newFrame, 0, 2);
        Ui.Place(body, columns);

        // Rows wrap differently on each side, so the side being scrolled places the same document line at the other's
        // top. Only the scrolled side drives: the follower's own clamping never pulls it back.
        static void Follow(ScintillaEditor from, ScintillaEditor to) => from.VerticalOffsetChanged += (_, _) =>
        {
            var (line, offset) = from.DocumentPosition;
            to.ScrollToDocumentPosition(line, offset);
        };
    }

    private EditorFrame Code(string name, Side side, string?[] labels)
    {
        var frame = new EditorFrame(side.Text.ToString(), name) { Margin = new Thickness(0, 8, 0, 0) };
        var editor = frame.Editor;
        editor.LabelLines(labels);
        editor.IsReadOnly = true;
        editor.WrapWidth = wrapWidth;
        editor.LineStyles = LineStyles();
        editor.StyleLines(side.Styles);
        frame.ThemeApplied += () => editor.LineStyles = LineStyles();
        frames.Add(frame);
        return frame;
    }

    private static int Gap(Hunk? previous, Hunk next) =>
        previous is null ? Math.Max(0, next.OldCount == 0 ? next.OldStart : next.OldStart - 1)
            : next.OldStart - (previous.OldStart + previous.OldCount);

    private static (List<string> Preamble, List<Hunk> Hunks) Parse(string diff)
    {
        var preamble = new List<string>();
        var hunks = new List<Hunk>();
        var lines = diff.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        Hunk? current = null;
        for (var index = 0; index < count; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var match = HunkHeader().Match(line);
            if (match.Success)
            {
                current = new(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1, int.Parse(match.Groups[3].Value), line, []);
                hunks.Add(current);
            }
            else if (current is null) preamble.Add(line);
            else current.Lines.Add(line);
        }
        return (preamble, hunks);
    }

    private static void IgnoreWhitespace(Hunk hunk)
    {
        var lines = hunk.Lines;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith('-')) continue;
            var removedEnd = index;
            while (removedEnd < lines.Count && lines[removedEnd].StartsWith('-')) removedEnd++;
            var addedEnd = removedEnd;
            while (addedEnd < lines.Count && lines[addedEnd].StartsWith('+')) addedEnd++;
            var removed = removedEnd - index;
            if (addedEnd - removedEnd == removed &&
                Enumerable.Range(0, removed).All(offset => Strip(lines[index + offset]) == Strip(lines[removedEnd + offset])))
            {
                var context = Enumerable.Range(0, removed).Select(offset => " " + lines[removedEnd + offset][1..]).ToArray();
                lines.RemoveRange(index, addedEnd - index);
                lines.InsertRange(index, context);
                index += removed - 1;
            }
            else index = addedEnd - 1;
        }
    }

    private static string Strip(string line) => WhitespaceRun().Replace(line[1..], "");

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}