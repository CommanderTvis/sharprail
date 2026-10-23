using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// Ghostty's undo close: a tab the user closed comes back in place within the host's undo timeout, a terminal with
/// its session still running. Each close has its own timeout and a later close never extends an earlier one; redo
/// closes the restored tab again.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private sealed record UndoableClose(ClosedTab Tab, DateTime Expires, long Toast);

    private readonly List<UndoableClose> undoCloses = [];
    private readonly List<ClosedTab> redoCloses = [];
    // Live terminal tabs closed for undo: their view only detaches and the host keeps their session for the timeout.
    private readonly HashSet<string> undoableTerminalCloses = [];
    private bool redoingClose;

    private void WireUndoClose()
    {
        Layout.TabClosing += RememberClosedTab;
        // Closing the window ends the sessions it kept for undo; the host ends any left when it stops.
        Closed += (_, _) =>
        {
            var pending = undoCloses.Where(entry => entry.Tab.Tab.Kind == "terminal").Select(entry => entry.Tab).ToArray();
            undoCloses.Clear(); redoCloses.Clear(); undoableTerminalCloses.Clear();
            var catalog = workbench.TerminalTabs;
            if (pending.Length > 0)
                _ = Task.Run(async () =>
                {
                    foreach (var closed in pending)
                        try { await catalog.CloseTabAsync(closed.Workspace, closed.Tab.Id); }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                });
        };
    }

    private void RememberClosedTab(ClosedTab closed)
    {
        if (!redoingClose) redoCloses.Clear();
        PruneUndoCloses();
        var seconds = state.Current.Settings.TerminalUndoCloseSeconds;
        if (seconds <= 0 || closed.Tab.IsTool) return;
        var key = closed.Workspace + ":" + closed.Tab.Id;
        if (closed.Tab.Kind == "terminal")
        {
            // Only a host-shared terminal whose shell still runs can come back with its process.
            if (!sharedWorkspaces.Contains(closed.Workspace) ||
                documentContent.GetValueOrDefault(key) is Terminal.TerminalView { IsExited: true } or Terminal.TerminalView { IsFailed: true }) return;
            undoableTerminalCloses.Add(key);
        }
        var timeout = TimeSpan.FromSeconds(seconds);
        var toast = Toasts.Push(ToastVariant.Info, "Closed " + closed.Tab.Title, duration: timeout,
            action: new ToastAction("Undo", () => UndoClose(closed)), countdown: true);
        undoCloses.Add(new(closed, DateTime.UtcNow + timeout, toast));
    }

    private void PruneUndoCloses()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in undoCloses.Where(entry => entry.Expires <= now).ToArray())
        {
            undoCloses.Remove(expired);
            undoableTerminalCloses.Remove(expired.Tab.Workspace + ":" + expired.Tab.Tab.Id);
        }
    }

    /// <summary>Whether undo would reopen a tab of the shown workspace now.</summary>
    internal bool CanUndoClose()
    {
        PruneUndoCloses();
        return undoCloses.Any(entry => entry.Tab.Workspace == Layout.State.ActiveWorkspace);
    }

    internal bool CanRedoClose() => redoCloses.Any(closed => closed.Workspace == Layout.State.ActiveWorkspace && Layout.GroupOf(closed.Tab.Id) is not null);

    /// <summary>Reopens the most recently closed tab of the shown workspace, or <paramref name="only"/>.</summary>
    internal void UndoClose(ClosedTab? only = null)
    {
        PruneUndoCloses();
        var entry = undoCloses.LastOrDefault(entry => only is null ? entry.Tab.Workspace == Layout.State.ActiveWorkspace : entry.Tab == only);
        if (entry is null) return;
        undoCloses.Remove(entry);
        Toasts.Dismiss(entry.Toast);
        // Reserving the tab again restores it on the host; its view then attaches to the session it left.
        if (Layout.Reopen(entry.Tab)) redoCloses.Add(entry.Tab);
        undoableTerminalCloses.Remove(entry.Tab.Workspace + ":" + entry.Tab.Tab.Id);
    }

    /// <summary>Closes the most recently reopened tab again, through the ordinary close so it is undoable once more.</summary>
    internal void RedoClose()
    {
        var closed = redoCloses.LastOrDefault(closed => closed.Workspace == Layout.State.ActiveWorkspace && Layout.GroupOf(closed.Tab.Id) is not null);
        if (closed is null) return;
        redoCloses.Remove(closed);
        redoingClose = true;
        try { Layout.Close(Layout.GroupOf(closed.Tab.Id)!, closed.Tab.Id); }
        finally { redoingClose = false; }
    }

    /// <summary>
    /// Whether the undo-close chords may act on this focus: a text box or editor keeps its own undo, and off macOS a terminal
    /// keeps Ctrl+Z (suspend) for its shell.
    /// </summary>
    internal bool UndoCloseFocusAllows(bool control)
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused) return true;
        if (focused is TextBox || focused.FindAncestorOfType<Editor.CodeDocumentView>(true) is not null) return false;
        return !control || focused.FindAncestorOfType<Terminal.TerminalView>(true) is null;
    }
}