using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private const string StaleDiff = "This file changed since you opened it — review the new diff";

    /// <summary>How long a revert's Undo receipt stays; checks under load lengthen it.</summary>
    public TimeSpan UndoWindow { get; set; } = TimeSpan.FromSeconds(8);

    // Staged and commit diffs end in the index or history, so nothing in them is the worktree's to restore.
    private Func<RevertTarget, Task>? DiffRevert(DockTab tab, string key) =>
        tab.Scope is "all" or "uncommitted" or "branch" or "untracked" ? target => RevertAsync(tab.Id, key, target) : null;

    private async Task RevertAsync(string tabId, string key, RevertTarget target)
    {
        var request = projectRequest;
        // A retargeted branch diff keeps its tab and view, so the comparison is read at the time of the click.
        if (Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values.SelectMany(tabs => tabs)
            .FirstOrDefault(tab => tab.Id == tabId) is not { } tab) return;
        var name = Path.GetFileName(tab.Path);
        try
        {
            // The block's lines are those of the diff this tab drew, so the sides' hashes are trusted only while the
            // host still answers with that diff; the host then refuses if either side moved after this read.
            var (sides, diff) = await Task.Run(async () => (
                await host.GetDiffSidesAsync(tab.Path, tab.Scope, tab.Comparison, lifetime.Token),
                await host.GetDiffAsync(tab.Path, tab.Scope, tab.Comparison, lifetime.Token)), lifetime.Token);
            if (request != projectRequest || !documents.TryGetValue(key, out var shown)) return;
            if (diff != shown.Text)
            {
                documents[key] = new(tab.Path, diff);
                if (documentContent.GetValueOrDefault(key) is DiffView view) view.Update(diff);
                Toasts.Push(ToastVariant.Info, StaleDiff);
                return;
            }
            var receipt = await host.RevertChangeAsync(tab.Path, tab.Scope, tab.Comparison, target, new(sides.OriginalHash, sides.ModifiedHash), lifetime.Token);
            Toasts.Push(ToastVariant.Success,
                !target.IsFile ? $"Reverted hunk in {name}" : receipt.Trashed is null ? $"Reverted {name}" : $"Moved {name} to the trash",
                duration: UndoWindow, action: new("Undo", () => _ = UndoAsync(receipt)));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { ReportChangeFailure(error, target.IsFile ? "Couldn't revert the file" : "Couldn't revert the hunk"); }
        if (request == projectRequest) await RefreshGitAsync(request);
    }

    private async Task UndoAsync(ChangeReceipt receipt)
    {
        var request = projectRequest;
        try { await host.UndoChangeAsync(receipt.Id, receipt.After.Hash, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { ReportChangeFailure(error, "Couldn't undo the revert"); }
        if (request == projectRequest) await RefreshGitAsync(request);
    }

    private void ReportChangeFailure(Exception error, string title)
    {
        if (error is ChangeException { Code: ChangeFailure.StaleView }) Toasts.Push(ToastVariant.Info, StaleDiff);
        else if (error is ChangeException { Code: ChangeFailure.ReceiptUnknown })
            Toasts.Push(ToastVariant.Info, "This change can no longer be undone — the host no longer holds it");
        else { Toasts.Push(ToastVariant.Error, error.Message, title); Console.Error.WriteLine(error); }
    }
}