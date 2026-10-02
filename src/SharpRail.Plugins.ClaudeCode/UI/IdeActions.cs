using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// Answers the IDE actions the host relays from a Claude CLI, with the app's editors. Always replies: a thrown error
/// becomes the CLI's tool error.
/// </summary>
internal sealed class IdeActions(IPluginUIContext context)
{
    // The IDE protocol sends absolute paths and the editors take worktree-relative ones (a plain POSIX prefix strip).
    private static string Relative(string workspaceId, string path)
    {
        var root = workspaceId.TrimEnd('/');
        if (path == root) return "";
        return path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..] : path;
    }

    private EditorRef? Find(string workspaceId, string? path)
    {
        var relative = Relative(workspaceId, path ?? "");
        return context.Editors.List(workspaceId).FirstOrDefault(editor => editor.Path == relative);
    }

    private async Task<object> RunAsync(IdeActionRequest request)
    {
        var workspaceId = request.WorkspaceId;
        var parameters = request.Params;
        switch (request.Kind)
        {
            case IdeActionKind.OpenFile:
                await context.Editors.OpenAsync(workspaceId, Relative(workspaceId, parameters.Path ?? ""), new() { Preview = parameters.Preview == true });
                return new { success = true };
            case IdeActionKind.OpenDiff:
                // Claude Code's openDiff proposes unsaved content for review, which diff tabs cannot show: they read both
                // sides from Git. Opening the file is the honest subset.
                var target = parameters.NewPath ?? parameters.OldPath ?? throw new InvalidOperationException("openDiff needs a file path");
                await context.Editors.OpenAsync(workspaceId, Relative(workspaceId, target));
                return new { success = true, diffShown = false };
            case IdeActionKind.GetOpenEditors:
                return new
                {
                    editors = context.Editors.List(workspaceId).Where(editor => editor.Kind is EditorKind.File or EditorKind.ExternalFile)
                        .Select(editor => new { path = editor.Path, isDirty = editor.Dirty }).ToArray()
                };
            case IdeActionKind.CheckDocumentDirty:
                var inspected = Find(workspaceId, parameters.Path);
                return new { success = inspected is not null, isDirty = inspected?.Dirty ?? false };
            case IdeActionKind.SaveDocument:
                if (Find(workspaceId, parameters.Path) is not { } saved) return new { success = false, saved = false };
                if (saved.Dirty) await context.Editors.SaveAsync(saved.Id);
                return new { success = true, saved = saved.Dirty && !context.Editors.IsDirty(saved.Id) };
            case IdeActionKind.CloseTab:
                // The editor list carries no display name, only the path a tab renders, so a tab is matched by its basename.
                var named = context.Editors.List(workspaceId).FirstOrDefault(editor => editor.Path.Split('/')[^1] == parameters.TabName)
                    ?? throw new InvalidOperationException($"No open tab named {parameters.TabName}");
                context.Editors.Close(named.Id);
                return new { success = true };
            case IdeActionKind.CloseAllDiffTabs:
                var diffs = context.Editors.List(workspaceId).Where(editor => editor.Kind == EditorKind.Diff).ToArray();
                foreach (var editor in diffs) context.Editors.Close(editor.Id);
                return new { success = true, closed = diffs.Length };
            default:
                throw new InvalidOperationException($"Unsupported IDE action: {request.Kind}");
        }
    }

    public async Task HandleAsync(IdeActionRequest request)
    {
        IdeActionResult result;
        try { result = new(true) { Value = JsonSerializer.SerializeToElement(await RunAsync(request), PluginJson.Options) }; }
        catch (Exception error) when (error is not OperationCanceledException) { result = new(false) { Error = error.Message }; }
        try { await context.RequestAsync(ClaudeCodeContract.ActionReply, new IdeActionReply(request.Id, result)); }
        catch (Exception error) when (error is PluginCallException or InvalidOperationException) { }
    }
}