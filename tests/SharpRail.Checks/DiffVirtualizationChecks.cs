using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.Scintilla;
using SharpRail.UI;
using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>The expensive-diff guard, bounded stacked sections, per-hunk realization and file-card virtualization.</summary>
internal static class DiffVirtualizationChecks
{
    internal static void Run()
    {
        Thresholds();
        if (!ScintillaEditor.IsSupported)
        {
            Console.WriteLine("PASS diff virtualization thresholds; editor-backed guard, bounded and hunk checks need the native editor");
            return;
        }
        Guard();
        Bounded();
        Hunks();
        Cards();
    }

    // One hunk of `context` unchanged lines followed by `changed` removed/added pairs.
    internal static string Diff(int context, int changed, int start = 1, string header = "")
    {
        var builder = new StringBuilder(header);
        builder.Append($"@@ -{start},{context + changed} +{start},{context + changed} @@\n");
        for (var index = 0; index < context; index++) builder.Append(" context ").Append(index).Append('\n');
        for (var index = 0; index < changed; index++) builder.Append("-old ").Append(index).Append('\n');
        for (var index = 0; index < changed; index++) builder.Append("+new ").Append(index).Append('\n');
        return builder.ToString();
    }

    private static string Hunked(int hunks) => string.Concat(Enumerable.Range(0, hunks).Select(index => Diff(6, 1, 1 + index * 40)));

    private static void Thresholds()
    {
        Require(DiffView.ExpensiveChangedLines == 2000 && DiffView.BoundedSourceLines == 1000, "The fork's thresholds are 2000 changed and 1000 source lines.");
        var limit = DiffView.Measure(Diff(10, 1000), Timeout.InfiniteTimeSpan, CancellationToken.None);
        Require(limit == (2000, 2010, true), "Measure counts removed plus added lines and all source lines: " + limit);
        Require(!DiffView.Expensive(false, limit.Changed, limit.Complete), "Exactly 2000 changed lines shows the diff.");
        var over = DiffView.Measure(Diff(0, 1001), Timeout.InfiniteTimeSpan, CancellationToken.None);
        Require(DiffView.Expensive(false, over.Changed, over.Complete), "2002 changed lines waits behind the notice.");
        Require(!DiffView.Expensive(true, 50000, false), "One-sided diffs are never guarded.");
        var late = DiffView.Measure(Diff(0, 100000), TimeSpan.FromTicks(1), CancellationToken.None);
        Require(!late.Complete && DiffView.Expensive(false, late.Changed, late.Complete), "A count that cannot finish in time is treated as expensive.");
        Require(!DiffView.Bounded(1000) && DiffView.Bounded(1001), "Sections over 1000 source lines are bounded.");
    }

    private static void Guard()
    {
        var memory = new DiffGuardMemory(() => { });
        var window = new Window { Width = 900, Height = 600 };
        window.Show();
        try
        {
            var opened = 0;
            var editable = new DiffGuardMemory(() => opened++);
            using (var small = new DiffView(Diff(0, 1100), "a.txt", 800, revert: _ => Task.CompletedTask, guard: editable))
            {
                window.Content = small; Settle();
                Require(small.IsGuarded && small.Counts.Changed == 2200 && !small.GetLogicalDescendants().OfType<ScintillaEditor>().Any(),
                    "A short diff over the threshold waits behind the notice without creating editors.");
                var open = small.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "DiffOpenFile");
                open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Require(opened > 0, "Open file opens the editable side.");
            }
            using (var historical = new DiffView(Diff(0, 1100), "a.txt", 800, guard: new DiffGuardMemory(() => { })))
            {
                window.Content = historical; Settle();
                Require(historical.IsGuarded && !historical.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DiffOpenFile"),
                    "A diff without an editable side offers only Show diff anyway.");
            }
            var added = "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,3000 @@\n" + string.Concat(Enumerable.Range(0, 3000).Select(index => "+line " + index + "\n"));
            using (var oneSided = new DiffView(added, "new.txt", 800))
            {
                window.Content = oneSided; Settle();
                Until(() => oneSided.GetLogicalDescendants().OfType<ScintillaEditor>().Any());
                Require(!oneSided.IsGuarded, "An added file is never guarded.");
            }
            var large = Diff(0, 5000);
            Require(large.Length > DiffView.SynchronousPrepareLimit, "The large fixture must take the background path.");
            var view = new DiffView(large, "a.txt", 800, guard: memory);
            window.Content = view;
            Until(() => view.IsGuarded);
            Require(!view.GetLogicalDescendants().OfType<ScintillaEditor>().Any(), "The background count shows the notice, not editors.");
            var show = view.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "DiffShowAnyway");
            show.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => view.GetLogicalDescendants().OfType<ScintillaEditor>().Any());
            Require(memory.ShownAnyway && !view.IsGuarded, "Show diff anyway draws the source and is remembered.");
            view.Dispose();
            var again = new DiffView(large, "a.txt", 800, guard: memory);
            window.Content = again;
            Until(() => again.GetLogicalDescendants().OfType<ScintillaEditor>().Any());
            Require(!again.IsGuarded, "The same tab's next view remembers Show diff anyway.");
            again.Dispose();
            var stale = new DiffView(large, "a.txt", 800, guard: new DiffGuardMemory());
            window.Content = stale;
            stale.Update(Diff(1, 1));
            Settle();
            Require(!stale.IsGuarded && stale.GetLogicalDescendants().OfType<ScintillaEditor>().Any(editor => editor.Text.Contains("new 0", StringComparison.Ordinal)),
                "A newer diff drops the stale background result.");
            stale.Dispose();
        }
        finally { window.Close(); }
        Console.WriteLine("PASS expensive diff guard: thresholds, one-sided exemption, background count, Show diff anyway memory, Open file and stale results");
    }

    private static ChangesReviewPane Pane(IReadOnlyList<GitChange> files, Func<string, string> diff, List<DiffView>? views = null) =>
        new("Changes", new ChangesReviewState(), true,
            _ => Task.FromResult(new GitSnapshot(true, "main", files, [], [])),
            (file, natural, triage, _) =>
            {
                var view = new DiffView(diff(file.Path), file.Path, 800, revert: _ => Task.CompletedTask, triage: triage, naturalHeight: natural, embedded: true);
                views?.Add(view);
                return Task.FromResult(view);
            },
            _ => { }, () => { }, () => { }, () => true, () => false, _ => { });

    private static void Bounded()
    {
        using var pane = Pane([new GitChange("big.txt", "M", " ", null, 1, 1), new GitChange("small.txt", "M", " ", null, 1, 1)],
            path => path == "big.txt" ? Diff(1200, 1) : Diff(900, 1));
        var window = new Window { Width = 900, Height = 700, Content = pane };
        window.Show();
        try
        {
            Until(() => pane.DiffFor("big.txt") is { IsBounded: true } && pane.DiffFor("big.txt")!.Bounds.Height > 0);
            var big = pane.DiffFor("big.txt")!;
            var editor = big.GetLogicalDescendants().OfType<ScintillaEditor>().Single(control => control.Name == "DiffNewText");
            Until(() => editor.VerticalScroll.Maximum > 0);
            Require(big.Bounds.Height < DiffView.BoundedViewportHeight + 80, "A bounded section has a bounded viewport: " + big.Bounds.Height);
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(view => view.Name == "ReviewScroll");
            var point = editor.TranslatePoint(new Point(120, 100), window)!.Value;
            window.MouseWheel(point, new Vector(0, -3));
            Until(() => editor.VerticalPixelOffset > 0);
            Require(scroll.Offset.Y == 0, "Wheeling a bounded section scrolls its own viewport, not the changeset.");
            scroll.Offset = new Vector(0, 800);
            Until(() => pane.DiffFor("small.txt") is { } small && small.Bounds.Height > 0);
            Require(!pane.DiffFor("small.txt")!.IsBounded && pane.DiffFor("small.txt")!.Bounds.Height > 900 * 10,
                "A section of 1000 source lines or fewer keeps natural height.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS bounded stacked sections: over 1000 source lines scroll in their own viewport, others grow naturally");
    }

    private static void Hunks()
    {
        using var pane = Pane([new GitChange("hunks.txt", "M", " ", null, 80, 80)], _ => Hunked(80));
        var window = new Window { Width = 900, Height = 400, Content = pane };
        window.Show();
        try
        {
            Until(() => pane.DiffFor("hunks.txt") is { } loaded && loaded.HunkRealization.Realized > 0);
            var view = pane.DiffFor("hunks.txt")!;
            var (slots, realized) = view.HunkRealization;
            Require(slots == 80 && realized < 20, $"Only hunks near the viewport realize: {realized} of {slots}.");
            Require(view.HunkHeights.Skip(40).All(height => !height.Measured && height.Height > 0), "Unrealized hunks keep an estimated height.");
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(control => control.Name == "ReviewScroll");
            var hosts = view.AnchorCandidates.ToArray();
            scroll.Offset = new Vector(0, hosts[40].TranslatePoint(default, scroll)!.Value.Y + scroll.Offset.Y);
            Until(() => hosts[40] is Border { Child: not null } && hosts[0] is Border { Child: null });
            Settle();
            var top = hosts[40].TranslatePoint(default, scroll)!.Value.Y;
            Require(Math.Abs(top) < 2, "The anchored hunk stays at the viewport top while neighbours realize: " + top);
            Require(view.HunkHeights[0].Measured, "A released hunk keeps its measured height.");
            EditorSelection? selected = null;
            view.SelectionChanged += (_, selection) => selected = selection;
            var editor = ((Border)hosts[40]).GetLogicalDescendants().OfType<ScintillaEditor>().Single(control => control.Name == "DiffNewText");
            var at = editor.Text.IndexOf("new 0", StringComparison.Ordinal);
            editor.SelectRange(at, 5);
            Until(() => selected is not null);
            Require(selected!.StartLine == 1 + 40 * 40 + 6 && selected.StartColumn == 1, "A realized hunk maps its selection to modified file lines: " + selected);
            Require(((Border)hosts[40]).GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DiffRevertBlock"),
                "Each realized hunk offers its own Revert.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS per-hunk realization: estimated and measured heights, release, scroll anchor, selection mapping and hunk Revert");
    }

    private static void Cards()
    {
        var files = Enumerable.Range(0, 200).Select(index => new GitChange($"file-{index:D3}.txt", "M", " ", null, 1, 1)).ToArray();
        using var pane = Pane(files, _ => Diff(3, 1));
        var window = new Window { Width = 900, Height = 600, Content = pane };
        window.Show();
        try
        {
            Border[] Sections() => pane.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "ReviewSection").ToArray();
            int Realized() => Sections().Count(border => border.Child is not null);
            Until(() => pane.DiffFor("file-000.txt") is not null);
            Require(Sections().Length == 200 && Realized() < 40, $"Only cards near the viewport hold chrome: {Realized()} of 200.");
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(control => control.Name == "ReviewScroll");
            scroll.Offset = new Vector(0, scroll.Extent.Height / 2);
            Until(() => Sections().Where(border => border.Child is not null).Any(border => string.CompareOrdinal((string)border.Tag!, "file-050.txt") > 0) &&
                pane.DiffFor("file-000.txt") is null);
            Require(Realized() < 40 && pane.GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "ReviewFile") == Realized(),
                "Scrolling recycles cards instead of accumulating them.");
            var card = Sections().First(border => border.Child is not null);
            var viewed = card.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "ReviewViewed");
            Require(Equals(viewed.Tag, card.Tag), "A recycled card is bound to its new file.");
            viewed.IsChecked = true;
            Require(pane.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ReviewProgress" && text.Text == "1 of 200 reviewed"),
                "A recycled card's Viewed mark belongs to its current file.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS file-card virtualization: bounded realized cards, recycling and rebinding on scroll");
    }
}