using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;
using SharpRail.UI.Resources;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly Dictionary<string, ChangesReviewState> reviewStates = [];

    private static string DocumentOwner(string key) => key.Split('\u001f')[0];

    private DockTab ChangesTab()
    {
        var file = DiffTab(new("", " ", " ", null, 0, 0));
        var scope = file.Scope == "untracked" ? "all" : file.Scope;
        var identity = scope == "branch" ? "" : file.Comparison;
        return new($"changes:{scope}:{identity}", selectedCommit is null ? "Changes · " + changeScope : "Changes · " + selectedCommit.ShortSha,
            "changes", Scope: scope, Comparison: file.Comparison);
    }

    internal void OpenCommitChanges(string workspace, string sha)
    {
        if (workspace != workspaceRoot || !WorkspaceMounted) return;
        var destination = AcceptNavigation(BeginNavigation());
        if (destination is null) return;
        var tab = new DockTab($"changes:commit:{sha}", "Changes · " + sha[..Math.Min(7, sha.Length)],
            "changes", Scope: "commit", Comparison: sha);
        Layout.Open(tab, true, destination);
    }

    private void OpenChanges(GitChange change)
    {
        var request = BeginNavigation();
        var destination = AcceptNavigation(request);
        if (destination is null) return;
        var tab = ChangesTab();
        var key = workspaceRoot + ":" + tab.Id;
        if (!reviewStates.TryGetValue(key, out var progress)) reviewStates[key] = progress = new();
        progress.ActivePath = change.Path;
        Layout.Open(tab, false, destination);
        if (documentContent.GetValueOrDefault(key) is ChangesReviewPane pane) pane.Reveal(change.Path);
    }

    private ChangesReviewPane ReviewDocument(DockTab tab, string key)
    {
        if (!reviewStates.TryGetValue(key, out var progress)) reviewStates[key] = progress = new();
        var workspace = workspaceRoot;
        string Target() => tab.Scope == "branch" ? comparison : tab.Comparison;
        string FileScope(GitChange change) => tab.Scope is "all" or "uncommitted" or "branch" && change.IndexStatus == "?" ? "untracked" : tab.Scope;
        ChangesReviewPane? pane = null;
        pane = new(tab.Title, progress, tab.Scope is "all" or "uncommitted" or "branch",
            async token =>
            {
                var request = projectRequest;
                if (workspace != workspaceRoot) throw new OperationCanceledException();
                var target = Target();
                var snapshot = await Task.Run(async () => await host.GetGitAsync(target, token, tab.Scope == "branch" ? "all" : tab.Scope), token);
                if (request != projectRequest || workspace != workspaceRoot || target != Target()) throw new OperationCanceledException();
                foreach (var sectionKey in documents.Keys.Where(sectionKey => sectionKey.StartsWith(key + '\u001f', StringComparison.Ordinal)).ToArray()) documents.Remove(sectionKey);
                return snapshot;
            },
            async (change, natural, triage, token) =>
            {
                var request = projectRequest;
                if (workspace != workspaceRoot) throw new OperationCanceledException();
                var target = Target();
                var fileTab = tab with { Path = change.Path, Scope = FileScope(change), Comparison = target };
                var sectionKey = key + '\u001f' + change.Path;
                var diff = documents.TryGetValue(sectionKey, out var cached) ? cached.Text
                    : await Task.Run(async () => await host.GetDiffAsync(change.Path, fileTab.Scope, target, token), token);
                if (request != projectRequest || workspace != workspaceRoot || target != Target() || token.IsCancellationRequested) throw new OperationCanceledException();
                documents[sectionKey] = new(change.Path, diff);
                if (pane?.DiffFor(change.Path) is { } retained)
                {
                    if (retained.Identity is not null) await DescribeDiffAsync(retained, fileTab, sectionKey);
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    if (ReferenceEquals(retained, pane.DiffFor(change.Path)))
                    {
                        retained.Update(diff);
                        return retained;
                    }
                }
                var view = DiffDocument(documents[sectionKey], fileTab, sectionKey, triage, natural, embedded: true);
                return view;
            },
            change => _ = OpenDiffAsync(change, tab with { Kind = "diff", Path = change.Path, Scope = FileScope(change), Comparison = Target() }),
            () =>
            {
                if (!LiveDocuments().Contains(key)) return;
                if (progress.Viewed.Count > 0 || progress.Kept.Values.Any(kept => kept.Count > 0))
                {
                    var group = Layout.View.Documents.FirstOrDefault(pair => pair.Value.Any(item => item.Id == tab.Id && item.Preview)).Key;
                    if (group is not null) Layout.Keep(group, tab.Id);
                }
                UpdateActiveChangeRows();
            },
            () =>
            {
                selectedCommit = null; changeScope = "All changes"; SaveGitSelection();
                _ = RefreshGitAsync(projectRequest);
                var all = ChangesTab(); Layout.Open(all, false);
            },
            () => Layout.Selected(Layout.View.FocusedGroup)?.Id == tab.Id,
            () => Preferences.ChangesSingle,
            value =>
            {
                Preferences.ChangesSingle = value;
                SaveProfile();
                foreach (var window in workbench.Windows)
                    foreach (var other in window.documentContent.Values.OfType<ChangesReviewPane>().Where(other => !ReferenceEquals(other, pane))) other.UpdateMode();
            });
        return pane;
    }

    private DiffView? SectionDiff(string key)
    {
        if (documentContent.GetValueOrDefault(DocumentOwner(key)) is not ChangesReviewPane pane) return null;
        return pane.DiffFor(key[(key.IndexOf('\u001f') + 1)..]);
    }

    private bool IsCurrentDiff(string key, DiffView view) => ReferenceEquals(documentContent.GetValueOrDefault(key), view) ||
        documentContent.GetValueOrDefault(DocumentOwner(key)) is ChangesReviewPane pane && pane.Contains(view);
}