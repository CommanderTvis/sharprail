using SharpRail.UI.Panels;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    // Live terminals whose removal was confirmed for the transition being retried.
    private readonly HashSet<string> approvedTerminalCloses = [];
    private bool askingToCloseTerminal;

    private TerminalView? LiveTerminal(string workspace, string tabId) =>
        documentContent.GetValueOrDefault(workspace + ":" + tabId) is TerminalView { IsExited: false, IsFailed: false } view ? view : null;

    private bool TerminalMayClose(string workspace, string tabId) =>
        LiveTerminal(workspace, tabId) is null || approvedTerminalCloses.Contains(workspace + ":" + tabId);

    // Returns whether every blocked terminal may now close; asks only when one runs a foreground process.
    private async Task<bool> ResolveTerminalsAsync(IReadOnlyList<(string Workspace, string TabId)> blocked)
    {
        var live = blocked.Where(item => !TerminalMayClose(item.Workspace, item.TabId)).ToArray();
        if (live.Length == 0) return true;
        if (askingToCloseTerminal) return false;
        askingToCloseTerminal = true;
        try
        {
            var busy = 0;
            foreach (var (workspace, tabId) in live)
                if (LiveTerminal(workspace, tabId) is { } view && await view.IsBusyAsync()) busy++;
            if (busy > 0 && !await Dialogs.Confirm(this, busy == 1 ? "Close this terminal?" : $"Close {busy} terminals?",
                    "A process is still running. Closing the tab terminates it.", "Close terminal", "TerminalCloseBusyConfirm"))
                return false;
            foreach (var (workspace, tabId) in live) approvedTerminalCloses.Add(workspace + ":" + tabId);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Report(error);
            return false;
        }
        finally { askingToCloseTerminal = false; }
    }
}