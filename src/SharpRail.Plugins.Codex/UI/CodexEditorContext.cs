using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The editor projection Codex's <c>/ide</c> reads: open file editors, the active one and its selection, retained per
/// workspace so focusing a terminal keeps its context. Diff views are never context; a closed editor drops its selection.
/// </summary>
public static class CodexEditorContext
{
    /// <summary>Codex positions are zero-based; editor selections are one-based, converted here exactly once.</summary>
    public static CodexIdeContext Build(IReadOnlyList<EditorRef> editors, EditorRef? active, EditorSelection? selection)
    {
        var files = editors.Where(editor => editor.Kind != EditorKind.Diff).ToArray();
        static CodexIdeFile Descriptor(EditorRef editor) => new(editor.Path.Split('/', '\\')[^1], editor.Path);
        var current = files.FirstOrDefault(editor => editor.Id == active?.Id);
        return new([.. files.Select(Descriptor)])
        {
            ActiveFile = current is null ? null : new CodexIdeActiveFile(Descriptor(current).Label, current.Path,
                new(new(Math.Max(0, (selection?.StartLine ?? 1) - 1), Math.Max(0, (selection?.StartColumn ?? 1) - 1)),
                    new(Math.Max(0, (selection?.EndLine ?? 1) - 1), Math.Max(0, (selection?.EndColumn ?? 1) - 1))),
                selection?.Text ?? "")
        };
    }

    private static bool AppFocused() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.Windows.Any(window => window.IsActive);

    /// <summary>Answers the host's IDE requests for the workspace this app is showing.</summary>
    public static void Register(IPluginUIContext context)
    {
        var selections = new Dictionary<string, EditorSelection?>();
        var activeEditors = new Dictionary<string, EditorRef>();
        void RememberActive()
        {
            if (context.Host().ActiveEditor is { Kind: not EditorKind.Diff } active) activeEditors[active.WorkspaceId] = active;
        }
        RememberActive();
        context.WatchHost(host => host.ActiveEditor, (_, _) => RememberActive());
        context.Editors.OnEvent(change =>
        {
            switch (change)
            {
                case EditorSelectionEvent selected:
                    selections[selected.Editor.Id] = selected.Selection;
                    activeEditors[selected.Editor.WorkspaceId] = selected.Editor;
                    break;
                case EditorLifecycleEvent { Lifecycle: EditorLifecycle.Closed } closed:
                    selections.Remove(closed.Editor.Id);
                    if (activeEditors.GetValueOrDefault(closed.Editor.WorkspaceId)?.Id == closed.Editor.Id) activeEditors.Remove(closed.Editor.WorkspaceId);
                    break;
            }
        });
        context.Subscribe(CodexContract.IdeRequest, request =>
        {
            // The fork answers once per frontend, each with its own focus. Here the active window answers for its
            // workspace and is focused only while the app is; a background window showing the workspace answers
            // unfocused, so the host's 1200 ms fallback still finds it when the active window shows another.
            var host = context.Host();
            var shownActive = host.ActiveWorkspaceId == request.WorkspaceId;
            var editors = context.Editors.List(request.WorkspaceId);
            if (!shownActive && editors.Count == 0 && host.ShownTerminalTabKeys.GetValueOrDefault(request.WorkspaceId) is not { Count: > 0 }) return;
            var active = (shownActive ? host.ActiveEditor : null) ?? activeEditors.GetValueOrDefault(request.WorkspaceId);
            var ideContext = Build(editors, active, active is null ? null : selections.GetValueOrDefault(active.Id));
            var focused = shownActive && AppFocused();
            _ = Reply();

            async Task Reply()
            {
                try { await context.RequestAsync(CodexContract.IdeReply, new CodexIdeReply(request.RequestId, request.WorkspaceId, focused, ideContext)); }
                catch (Exception) { }
            }
        });
    }
}