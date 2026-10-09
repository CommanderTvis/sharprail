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
using SharpRail.UI.Resources;

namespace SharpRail.UI.Rendering;

/// <summary>One view of a diff tab. The source diff is drawn by the pane itself and has no <paramref name="Render"/>.</summary>
internal sealed record DiffChoice(string Id, string Label, Func<CancellationToken, Task<Control?>>? Render)
{
    internal static DiffChoice Source { get; } = new(ResourceRegistry.Code, "Source", null);
}

internal sealed partial class DiffView : Grid, IDisposable
{
    private sealed record Hunk(int OldStart, int OldCount, int NewStart, string Header, List<string> Lines);

    private readonly Grid body = new();
    private readonly ToggleButton split = Toggle("DiffSplit", "layout", "Side-by-side diff");
    private readonly ToggleButton inline = Toggle("DiffInline", "list", "Inline diff");
    private readonly ToggleButton whitespace = Toggle("DiffWhitespace", "collapseVertical", "Hide whitespace changes");
    private readonly StackPanel segments = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Dictionary<string, ToggleButton> toggles = [];
    private readonly Button copy;
    private readonly double wrapWidth;
    private readonly Action<string>? selectedChanged;
    private IReadOnlyList<DiffChoice> choices;
    private DiffChoice current;
    private bool pending;
    private CancellationTokenSource? merge;
    private Control? merged;
    private readonly List<EditorFrame> frames = [];
    private bool disposed;
    private string text;

    /// <summary>Whether a unified diff says Git saw bytes it will not show as lines.</summary>
    internal static bool IsBinaryDiff(string diff) => diff.Contains("Binary files ", StringComparison.Ordinal) && !diff.Contains("\n@@", StringComparison.Ordinal);

    /// <summary>
    /// A diff tab's pane. <paramref name="choices"/> are the resource renderers that match the file, in rank order,
    /// and become the view toggle; a tab with only the source diff offers split and inline instead. A choice that
    /// renders null cannot show this content, and the next one takes over. A <paramref name="pending"/> pane waits
    /// for <see cref="SetChoices"/> before it draws anything.
    /// </summary>
    internal DiffView(string text, string path, double wrapWidth,
        IReadOnlyList<DiffChoice>? choices = null, string? selected = null, Action<string>? selectedChanged = null, bool pending = false)
    {
        this.text = text;
        this.wrapWidth = wrapWidth;
        this.selectedChanged = selectedChanged;
        this.pending = pending;
        this.choices = choices is { Count: > 0 } ? choices : [DiffChoice.Source];
        current = this.choices.FirstOrDefault(choice => choice.Id == selected) ?? this.choices[0];
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
        copy = new Button
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
        controls.Children.AddRange([split, inline, whitespace, copy, segments]);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        Ui.Place(header, chip); Ui.Place(header, controls, 0, 1);
        Ui.Place(this, header);
        Ui.Place(this, body, 1);
        split.IsChecked = true; inline.IsChecked = false;
        split.Click += (_, _) => { split.IsChecked = true; inline.IsChecked = false; Render(); };
        inline.Click += (_, _) => { inline.IsChecked = true; split.IsChecked = false; Render(); };
        whitespace.Click += (_, _) => Render();
        BuildSegments();
        Render();
    }

    internal bool IsRendered => current.Render is not null;

    /// <summary>What the choices were resolved from, for a pane whose content is identified by more than its diff text.</summary>
    internal string? Identity { get; private set; }

    internal void SetChoices(IReadOnlyList<DiffChoice> next, string? selected, string? identity)
    {
        choices = next; pending = false; Identity = identity;
        current = choices.FirstOrDefault(choice => choice.Id == selected) ?? choices[0];
        BuildSegments();
        Render();
    }

    internal void ShowError(string message)
    {
        pending = false;
        merge?.Cancel(); merge?.Dispose(); merge = null;
        ShowBody(Placeholder("DiffError", message, Ui.Danger));
    }

    private void BuildSegments()
    {
        segments.Children.Clear(); toggles.Clear();
        if (choices.Count < 2) return;
        foreach (var choice in choices)
        {
            var toggle = ResourcePane.Segment("DiffView_" + choice.Id[(choice.Id.LastIndexOf('/') + 1)..], choice.Label);
            toggle.Click += (_, _) => Choose(choice);
            toggles[choice.Id] = toggle;
            segments.Children.Add(toggle);
        }
    }

    internal void Update(string diff)
    {
        if (diff == text) return;
        text = diff;
        if (IsRendered) StartMerge(keepCurrent: true);
        else Render();
    }

    private void Choose(DiffChoice choice)
    {
        if (choice == current) { Check(); return; }
        current = choice;
        selectedChanged?.Invoke(choice.Id);
        Render();
    }

    private void Check()
    {
        foreach (var (id, toggle) in toggles) toggle.IsChecked = id == current.Id;
    }

    // Merges run off the UI thread; a newer merge or leaving the rendered view cancels the stale one.
    private void StartMerge(bool keepCurrent)
    {
        merge?.Cancel(); merge?.Dispose();
        var cancellation = merge = new CancellationTokenSource();
        var render = current.Render!;
        if (!keepCurrent || merged is null) ShowBody(Placeholder("RenderedDiffLoading", "Rendering diff…", Ui.Muted));
        _ = Complete();

        async Task Complete()
        {
            Control? result;
            try { result = await render(cancellation.Token); }
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

    // Leaves the tab's choice alone, so reopening it after the content changes renders again.
    private void RenderedUnavailable()
    {
        if (toggles.GetValueOrDefault(current.Id) is { } toggle)
        {
            toggle.IsEnabled = false;
            ToolTip.SetTip(toggle, "This view cannot show the content");
        }
        var fallback = choices.SkipWhile(choice => choice != current).Skip(1).Concat(choices)
            .FirstOrDefault(choice => choice != current && toggles.GetValueOrDefault(choice.Id)?.IsEnabled != false);
        if (fallback is null) { ShowBody(Placeholder("DiffError", "This content cannot be shown.", Ui.Muted)); return; }
        current = fallback;
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
        Check();
        // The source view of a file with other views is always side by side; Scintilla is macOS-only.
        split.IsVisible = inline.IsVisible = choices.Count == 1 && !IsRendered && OperatingSystem.IsMacOS();
        whitespace.IsVisible = !IsRendered && OperatingSystem.IsMacOS();
        copy.IsVisible = choices.Any(choice => choice.Render is null);
        if (pending) { ShowBody(Placeholder("DiffLoading", "Loading…", Ui.Muted)); return; }
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