using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>SharpRail's own checks of the toast queue and of reverting from a diff tab; no upstream scenario is translated here.</summary>
internal static class ChangeActionChecks
{
    internal static void Run(string root)
    {
        Queue();
        using var git = new IsolatedGit(Path.Combine(root, "git"));
        var previous = Environment.GetEnvironmentVariable("SHARPRAIL_TRASH_DIR");
        Environment.SetEnvironmentVariable("SHARPRAIL_TRASH_DIR", Path.Combine(root, "trash"));
        try
        {
            ToastCards(Path.Combine(root, "toasts"));
            Reverts(Path.Combine(root, "reverts"));
        }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_TRASH_DIR", previous); }
    }

    private static void Queue()
    {
        var queue = new ToastQueue();
        var changes = 0;
        queue.Changed += () => changes++;
        var first = queue.Push(ToastVariant.Info, "saved");
        Require(queue.Push(ToastVariant.Info, "saved") == first && queue.Items.Count == 1 && changes == 1,
            "An identical notification must coalesce into the visible one.");
        Require(queue.Push(ToastVariant.Error, "saved") != first && queue.Push(ToastVariant.Info, "saved", "Title") != first &&
            queue.Push(ToastVariant.Info, "saved", duration: TimeSpan.FromSeconds(1)) != first && queue.Items.Count == 4,
            "A different variant, title or duration is a different notification.");
        var undo = new ToastAction("Undo", () => { });
        var receipts = Enumerable.Range(0, 2).Select(_ => queue.Push(ToastVariant.Success, "Reverted a.txt", action: undo)).ToArray();
        Require(receipts[0] != receipts[1], "Actionable toasts must never coalesce: their actions name different inverses.");
        Require(queue.Items.Count == ToastQueue.Capacity && queue.Items.All(item => item.Id != first) && receipts.All(id => queue.Items.Any(item => item.Id == id)),
            "The cap must evict the oldest actionless toast.");
        for (var index = 0; index < 6; index++) queue.Push(ToastVariant.Success, "receipt " + index, action: undo);
        Require(queue.Items.Count == 8 && queue.Items.All(item => item.Action is not null), "The cap must evict only actionless toasts, so receipts stay.");
        var dropped = queue.Push(ToastVariant.Info, "over the cap");
        Require(queue.Items.All(item => item.Id != dropped), "An actionless toast has no room among more receipts than the cap.");
        queue.Dismiss(receipts[0]);
        Require(queue.Items.All(item => item.Id != receipts[0]) && queue.Items.Count == 7, "Dismissal must remove exactly that toast.");
        Require(new Toast(1, ToastVariant.Error, "x").Lifetime is null && new Toast(1, ToastVariant.Info, "x").Lifetime == ToastQueue.DefaultDuration &&
            new Toast(1, ToastVariant.Error, "x", Duration: TimeSpan.FromSeconds(8)).Lifetime == TimeSpan.FromSeconds(8),
            "Errors stay until dismissed unless a toast carries its own duration.");
        Console.WriteLine("PASS toast queue coalesces identical notifications, caps actionless ones at five and keeps receipts");
    }

    private static Border[] Cards(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "Toast").ToArray();

    private static T Part<T>(Control owner, string name) where T : Control =>
        owner.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static string[] Messages(E2eWorkspace app) => Cards(app).Select(card => Part<TextBlock>(card, "ToastMessage").Text!).ToArray();

    private static void ToastCards(string directory)
    {
        using var app = WorkspaceFixture.OpenFixtureProject(directory);
        var toasts = app.Window.Toasts;
        Require(Cards(app).Length == 0, "A window starts without toasts.");
        var invoked = 0;
        toasts.Push(ToastVariant.Error, "The host refused.", "Couldn't revert the file");
        toasts.Push(ToastVariant.Success, "Reverted a.txt", duration: TimeSpan.FromSeconds(30), action: new("Undo", () => invoked++));
        var brief = toasts.Push(ToastVariant.Info, "Brief", duration: TimeSpan.FromMilliseconds(300));
        Until(() => Cards(app).Length == 3);
        var cards = Cards(app);
        Require(Part<TextBlock>(cards[0], "ToastTitle").Text == "Couldn't revert the file" && Messages(app).SequenceEqual(["The host refused.", "Reverted a.txt", "Brief"]),
            "Toasts must stack oldest first with their title and message.");
        Require(cards[0].Bounds.Width == 356 && cards.All(card => card.Bounds.Height > 0), "Toasts take the notification width.");
        Until(() => toasts.Items.All(item => item.Id != brief) && Cards(app).Length == 2);
        Require(ReferenceEquals(Cards(app)[0], cards[0]) && ReferenceEquals(Cards(app)[1], cards[1]), "Another toast ending must not rebuild the remaining cards.");
        app.Click(Part<Button>(cards[1], "ToastAction"));
        Until(() => invoked == 1 && Cards(app).Length == 1);
        app.Click(Part<Button>(cards[0], "ToastDismiss"));
        Until(() => Cards(app).Length == 0 && toasts.Items.Count == 0);
        Console.WriteLine("PASS toasts render stacked cards with their own duration, one action and dismissal");
    }

    private static Button[] BlockReverts(E2eWorkspace app)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return Pane(app)?.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "DiffRevertBlock" && button.IsVisible)
            .OrderBy(button => Canvas.GetTop(button)).ToArray() ?? [];
    }

    private static Button? FileRevert(E2eWorkspace app) =>
        Pane(app)?.GetLogicalDescendants().OfType<Button>().SingleOrDefault(button => button.Name == "DiffRevertFile");

    private static void OpenDiff(E2eWorkspace app, string path)
    {
        ClickRow(app, path);
        Until(() => DiffTabs(app).Any(tab => tab.Path == path) && Pane(app) is not null &&
            app.Window.Layout.Selected(app.Center) is { Kind: "diff" } selected && selected.Path == path);
    }

    private static void Undo(E2eWorkspace app, string message)
    {
        Until(() => Messages(app).Contains(message));
        var card = Cards(app).Single(card => Part<TextBlock>(card, "ToastMessage").Text == message);
        Require(((Toast)card.Tag!).Lifetime == TimeSpan.FromSeconds(8), "An Undo receipt stays for its own few seconds.");
        app.Click(Part<Button>(card, "ToastAction"));
    }

    private static void Reverts(string directory)
    {
        using var app = WorkspaceFixture.OpenFixtureProject(directory);
        var lines = Enumerable.Range(1, 40).Select(index => "line " + index).ToArray();
        string Write(string name, params (int Line, string Text)[] edits)
        {
            var text = (string[])lines.Clone();
            foreach (var (line, value) in edits) text[line - 1] = value;
            var content = string.Join("\n", text) + "\n";
            File.WriteAllText(Path.Combine(app.Root, name), content);
            return content;
        }
        string Read(string name) => File.ReadAllText(Path.Combine(app.Root, name));
        var committed = Write("blocks.txt");
        IsolatedGit.Run(app.Root, "add", "-A");
        IsolatedGit.Run(app.Root, "commit", "-m", "blocks");
        DefaultWorkspaceE2E.EnterDefaultWorkspace(app);
        var edited = Write("blocks.txt", (5, "first change"), (30, "second change"));
        File.WriteAllText(Path.Combine(app.Root, "new.txt"), "added\n");
        ShowChanges(app);
        UntilRows(app, "blocks.txt", "new.txt");

        OpenDiff(app, "blocks.txt");
        Until(() => FileRevert(app) is not null);
        if (OperatingSystem.IsMacOS())
        {
            Until(() => BlockReverts(app).Length == 2);
            var targets = BlockReverts(app).Select(button => (RevertTarget)button.Tag!).ToArray();
            Require(targets[0] == new RevertTarget(new(5, 1), new(5, 1)) && targets[1] == new RevertTarget(new(30, 1), new(30, 1)),
                "Each change block must carry its own line spans on both sides.");
            app.Click(BlockReverts(app)[0]);
            Until(() => Read("blocks.txt") == Write("expected.tmp", (30, "second change")));
            Until(() => BlockReverts(app).Length == 1 && !DiffText(app).Contains("first change", StringComparison.Ordinal));
            Undo(app, "Reverted hunk in blocks.txt");
            Until(() => Read("blocks.txt") == edited && BlockReverts(app).Length == 2);
            File.Delete(Path.Combine(app.Root, "expected.tmp"));
            Console.WriteLine("PASS a change block reverts alone from its diff and Undo restores it");
        }

        // The file moves after the tab drew it: nothing is reverted and the tab shows the new diff.
        var moved = "";
        app.Host.BeforeRevert = () => moved = Write("blocks.txt", (5, "first change"), (12, "moved under the view"), (30, "second change"));
        app.Click(FileRevert(app)!);
        Until(() => Messages(app).Contains("This file changed since you opened it — review the new diff"));
        app.Host.BeforeRevert = null;
        Require(moved.Length > 0 && Read("blocks.txt") == moved, "A stale view must not revert anything.");
        UntilDiff(app, text => text.Contains("moved under the view", StringComparison.Ordinal));
        Console.WriteLine("PASS a diff that changed since it was drawn reloads with a notice instead of reverting");

        app.Click(FileRevert(app)!);
        Until(() => Read("blocks.txt") == committed);
        Undo(app, "Reverted blocks.txt");
        Until(() => Read("blocks.txt") == moved);
        UntilDiff(app, text => text.Contains("moved under the view", StringComparison.Ordinal));
        Console.WriteLine("PASS Revert file restores the original side and Undo brings the edit back");

        OpenDiff(app, "new.txt");
        Until(() => FileRevert(app) is not null);
        Require(BlockReverts(app).Length == 0, "A file that is all added has no block to revert, only the file.");
        app.Click(FileRevert(app)!);
        Until(() => !File.Exists(Path.Combine(app.Root, "new.txt")));
        Undo(app, "Moved new.txt to the trash");
        Until(() => File.Exists(Path.Combine(app.Root, "new.txt")) && Read("new.txt") == "added\n");
        Console.WriteLine("PASS reverting a new file moves it to the trash and Undo puts it back");

        IsolatedGit.Run(app.Root, "add", "blocks.txt");
        PickScope(app, "Staged");
        UntilRows(app, "blocks.txt");
        OpenDiff(app, "blocks.txt");
        Until(() => DiffTabs(app).Any(tab => tab.Scope == "staged") && Pane(app) is not null);
        Settle();
        Require(FileRevert(app) is null && BlockReverts(app).Length == 0, "A staged diff's modified side is the index, so it offers no revert.");
        Console.WriteLine("PASS a diff whose modified side is not the worktree offers no revert");
    }
}