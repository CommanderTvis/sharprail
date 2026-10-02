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

    private static string Absolute(string workspaceId, string path) => Path.IsPathRooted(path) ? path : Path.Combine(workspaceId, path);

    // The one-based line where the text first occurs in the file, or nothing when it does not or the file is unreadable.
    private async Task<int?> LineOfAsync(string workspaceId, string path, string? text)
    {
        if (string.IsNullOrEmpty(text) || Path.IsPathRooted(path)) return null;
        try
        {
            var content = System.Text.Encoding.UTF8.GetString(await context.ReadFileAsync(workspaceId, path));
            var found = content.IndexOf(text, StringComparison.Ordinal);
            return found < 0 ? null : content.AsSpan(0, found).Count('\n') + 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PluginCallException) { return null; }
    }

    private async Task<object> RunAsync(IdeActionRequest request)
    {
        var workspaceId = request.WorkspaceId;
        var parameters = request.Params;
        switch (request.Kind)
        {
            case IdeActionKind.OpenFile:
                var path = Relative(workspaceId, parameters.Path ?? "");
                // The protocol selects from startText to endText; revealing where the range starts is what the editors offer.
                if (await context.Editors.OpenAsync(workspaceId, path, new()
                { Preview = parameters.Preview == true, Line = await LineOfAsync(workspaceId, path, parameters.StartText) }) is null)
                    throw new InvalidOperationException("The session's workspace is not open in a SharpRail window");
                return new { success = true };
            case IdeActionKind.OpenDiff:
                // Claude Code's openDiff proposes unsaved content for review, which diff tabs cannot show: they read both
                // sides from Git. Opening the file is the honest subset; the CLI then asks in its own terminal.
                var target = parameters.NewPath ?? parameters.OldPath ?? throw new InvalidOperationException("openDiff needs a file path");
                await context.Editors.OpenAsync(workspaceId, Relative(workspaceId, target));
                return new { success = true, diffShown = false };
            case IdeActionKind.GetOpenEditors:
                var active = context.Editors.Active?.Id;
                return new
                {
                    tabs = context.Editors.List(workspaceId).Where(editor => editor.Kind is EditorKind.File or EditorKind.ExternalFile).Select(editor => new
                    {
                        uri = "file://" + Absolute(workspaceId, editor.Path),
                        isActive = editor.Id == active,
                        label = editor.Path.Split('/')[^1],
                        isDirty = editor.Dirty
                    }).ToArray()
                };
            case IdeActionKind.CheckDocumentDirty:
                return Find(workspaceId, parameters.Path) is { } inspected
                    ? new { success = true, filePath = parameters.Path, isDirty = inspected.Dirty, isUntitled = false }
                    : new { success = false, message = "Document not open: " + parameters.Path };
            case IdeActionKind.SaveDocument:
                if (Find(workspaceId, parameters.Path) is not { } saved) return new { success = false, message = "Document not open: " + parameters.Path };
                if (saved.Dirty) await context.Editors.SaveAsync(saved.Id);
                return new { success = true, filePath = parameters.Path, saved = saved.Dirty && !context.Editors.IsDirty(saved.Id) };
            case IdeActionKind.CloseTab:
                // The CLI names the tab it asked openDiff for, or a file by its absolute path; a file name alone also closes its tab.
                var name = parameters.TabName ?? "";
                var named = context.Editors.List(workspaceId).FirstOrDefault(editor =>
                    Absolute(workspaceId, editor.Path) == name || editor.Path.Split('/')[^1] == name);
                if (named is not null) context.Editors.Close(named.Id);
                return new { closed = named is null ? 0 : 1 };
            case IdeActionKind.CloseAllDiffTabs:
                var diffs = context.Editors.List(workspaceId).Where(editor => editor.Kind == EditorKind.Diff).ToArray();
                foreach (var editor in diffs) context.Editors.Close(editor.Id);
                return new { closed = diffs.Length };
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