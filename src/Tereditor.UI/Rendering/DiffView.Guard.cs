using System.Diagnostics;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;

namespace SharpRail.UI.Rendering;

/// <summary>
/// Where a diff tab remembers that its reader chose Show diff anyway, and how it opens the editable file.
/// A standalone tab and each Changes review section own one, so the choice survives remounts and refreshes of
/// that tab only. <see cref="OpenFile"/> is null when no side of the diff is the worktree's.
/// </summary>
internal sealed class DiffGuardMemory(Action? openFile = null)
{
    internal bool ShownAnyway { get; set; }
    internal Action? OpenFile { get; } = openFile;
}

internal sealed partial class DiffView
{
    /// <summary>Above this many added plus removed lines a source diff waits behind the expensive-diff notice.</summary>
    internal const int ExpensiveChangedLines = 2000;

    /// <summary>A stacked section with more source lines than this, unchanged context included, gets a bounded viewport.</summary>
    internal const int BoundedSourceLines = 1000;

    /// <summary>The bounded viewport's height; the editor scrolls inside it and draws only its visible lines.</summary>
    internal const double BoundedViewportHeight = 640;

    /// <summary>How long the background counting parse may take before the diff is treated as expensive.</summary>
    internal static readonly TimeSpan GuardBudget = TimeSpan.FromMilliseconds(250);

    // Short diffs are parsed where they are drawn: the work is bounded and keeps small views synchronous. Longer ones
    // are parsed and counted on the thread pool, and only the result is applied on the dispatcher.
    internal const int SynchronousPrepareLimit = 64 * 1024;

    private sealed record Prepared(List<string> Preamble, List<Hunk> Hunks, IReadOnlyDictionary<RevertTarget, string> Keys,
        int ChangedLines, int SourceLines, bool Complete);

    private readonly DiffGuardMemory guard;
    private CancellationTokenSource? preparing;

    /// <summary>Whether the view currently shows the expensive-diff notice instead of the source.</summary>
    internal bool IsGuarded { get; private set; }

    /// <summary>Whether the stacked source sits in a bounded viewport with its own scrolling.</summary>
    internal bool IsBounded { get; private set; }

    /// <summary>The counts of the last prepared source diff: added plus removed lines and all hunk lines.</summary>
    internal (int Changed, int Source) Counts { get; private set; }

    /// <summary>Whether a diff with these counts waits behind the notice. One-sided diffs never do.</summary>
    internal static bool Expensive(bool singleSided, int changedLines, bool complete) =>
        !singleSided && (changedLines > ExpensiveChangedLines || !complete);

    /// <summary>Whether a stacked section of this many source lines gets a bounded viewport.</summary>
    internal static bool Bounded(int sourceLines) => sourceLines > BoundedSourceLines;

    /// <summary>Counts a unified diff's changed and source lines, stopping when the budget runs out.</summary>
    internal static (int Changed, int Source, bool Complete) Measure(string diff, TimeSpan budget, CancellationToken token)
    {
        var (_, hunks, complete) = ParseWithin(diff, budget, token);
        var (changed, source) = Count(hunks);
        return (changed, source, complete);
    }

    private static (int Changed, int Source) Count(List<Hunk> hunks)
    {
        int changed = 0, source = 0;
        foreach (var hunk in hunks)
            foreach (var line in hunk.Lines)
            {
                if (line.StartsWith('\\')) continue;
                source++;
                if (line is ['+' or '-', ..]) changed++;
            }
        return (changed, source);
    }

    private static (List<string> Preamble, List<Hunk> Hunks, bool Complete) ParseWithin(string diff, TimeSpan budget, CancellationToken token)
    {
        if (budget == Timeout.InfiniteTimeSpan) { var (preamble, hunks) = Parse(diff); return (preamble, hunks, true); }
        var clock = Stopwatch.StartNew();
        var partialPreamble = new List<string>();
        var partial = new List<Hunk>();
        Hunk? current = null;
        var start = 0;
        for (var lines = 0; start < diff.Length; lines++)
        {
            if ((lines & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed > budget) return (partialPreamble, partial, false);
            }
            var end = diff.IndexOf('\n', start);
            if (end < 0) end = diff.Length;
            var line = diff[start..end].TrimEnd('\r');
            start = end + 1;
            var match = HunkHeader().Match(line);
            if (match.Success)
            {
                current = new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1,
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), line, []);
                partial.Add(current);
            }
            else if (current is null) partialPreamble.Add(line);
            else current.Lines.Add(line);
        }
        return (partialPreamble, partial, true);
    }

    private static Prepared Prepare(string diff, bool ignoreWhitespace, bool keys, TimeSpan budget, CancellationToken token)
    {
        var (preamble, hunks, complete) = ParseWithin(diff, budget, token);
        if (ignoreWhitespace) foreach (var hunk in hunks) IgnoreWhitespace(hunk);
        token.ThrowIfCancellationRequested();
        var (changed, source) = Count(hunks);
        return new(preamble, hunks, keys && complete ? ContentKeys(diff) : new Dictionary<RevertTarget, string>(), changed, source, complete);
    }

    // Small diffs prepare inline; larger ones on the thread pool with a budget. A newer render, a text change or
    // disposal cancels the stale preparation, and its result is dropped.
    private void RenderSource()
    {
        preparing?.Cancel(); preparing?.Dispose(); preparing = null;
        var diff = text;
        var ignore = whitespace.IsChecked == true;
        var keys = triage is not null;
        var budget = guard.ShownAnyway ? Timeout.InfiniteTimeSpan : GuardBudget;
        if (diff.Length <= SynchronousPrepareLimit)
        {
            Show(Prepare(diff, ignore, keys, Timeout.InfiniteTimeSpan, CancellationToken.None));
            return;
        }
        var cancellation = preparing = new CancellationTokenSource();
        Ui.Place(body, Placeholder("DiffLoading", "Loading…", Ui.Muted));
        _ = Complete();

        async Task Complete()
        {
            Prepared result;
            try { result = await Task.Run(() => Prepare(diff, ignore, keys, budget, cancellation.Token), cancellation.Token); }
            catch (OperationCanceledException) { return; }
            if (disposed || cancellation.IsCancellationRequested || !ReferenceEquals(preparing, cancellation) || diff != text) return;
            preparing = null; cancellation.Dispose();
            body.Children.Clear();
            Show(result);
        }
    }

    private void Show(Prepared prepared)
    {
        Counts = (prepared.ChangedLines, prepared.SourceLines);
        IsGuarded = !guard.ShownAnyway && Expensive(singleSided, prepared.ChangedLines, prepared.Complete);
        IsBounded = false;
        if (IsGuarded) { ShowGuard(prepared); return; }
        contentKeys = prepared.Keys;
        triage?.ReportKeys(contentKeys.Values.ToArray());
        var hunks = prepared.Hunks;
        if (hunks.Count == 0 && string.IsNullOrWhiteSpace(text))
        {
            var empty = Ui.Text("No differences between the two sides.", Ui.Hint, 12);
            empty.Name = "DiffEmpty"; empty.Margin = new Thickness(24); empty.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Place(body, empty); return;
        }
        if (!ScintillaEditor.IsSupported)
        {
            Ui.Place(body, new ScrollViewer { Content = MarkdownPreview.Code(text), Margin = new Thickness(24, 8), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            return;
        }
        IsBounded = naturalHeight && Bounded(prepared.SourceLines);
        var sideBySide = hunks.Count > 0 && split.IsChecked == true;
        if (naturalHeight && !IsBounded && hunks.Count > 1) RenderHunks(prepared.Preamble, hunks, sideBySide);
        else if (!sideBySide) Ui.Place(body, InlineBody(prepared.Preamble, hunks, null));
        else Ui.Place(body, SplitBody(hunks, null));
        if (IsBounded) body.Height = BoundedViewportHeight + 8;
    }

    // The fork's expensive-diff notice: the source waits until the reader asks for it.
    private void ShowGuard(Prepared prepared)
    {
        var message = prepared.Complete
            ? string.Create(CultureInfo.InvariantCulture, $"This diff changes {prepared.ChangedLines:N0} lines and is too expensive to show automatically.")
            : "This diff is too expensive to show automatically.";
        var notice = new StackPanel { Name = "DiffExpensiveNotice", Spacing = 8, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left };
        notice.Children.Add(Ui.Text(message, Ui.Hint, 12));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var show = Ui.Button("Show diff anyway", () => { guard.ShownAnyway = true; Render(); });
        show.Name = "DiffShowAnyway";
        actions.Children.Add(show);
        if (guard.OpenFile is { } open && revert is not null)
        {
            var file = Ui.Button("Open file", open);
            file.Name = "DiffOpenFile";
            actions.Children.Add(file);
        }
        notice.Children.Add(actions);
        Ui.Place(body, notice);
    }
}