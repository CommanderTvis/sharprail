using System.Text;
using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core.Language;

/// <summary>
/// What an agent's edit hook hears after it edits a file (Claude Code's PostToolUse on Edit, Write and NotebookEdit): the
/// diagnostics of the edited files from the language servers already running for their checkout, errors first, then
/// warnings, at most <see cref="Cap"/> lines, plus one line per file whose server is not running. It never starts a server
/// and answers within <see cref="Deadline"/>. Nothing to say is <see langword="null"/>, so clean edits stay quiet.
/// </summary>
public static class EditDiagnostics
{
    /// <summary>Most diagnostic lines one edit reports; the rest is counted.</summary>
    internal const int Cap = 20;

    /// <summary>How long the scan itself may take before unfinished files are reported pending. Settable for checks.</summary>
    internal static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>The hard limit of one answer, kept under the hook script's own 9 s; reached, the answer is one note. Settable for checks.</summary>
    internal static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(8);

    private const int MessageLimit = 300;

    /// <summary>
    /// The context for the agent after the edit described by the hook input, or <see langword="null"/> when there is nothing to
    /// say. Files outside the terminal's project, and files no language server handles, are left out silently.
    /// </summary>
    public static async Task<string?> ContextAsync(HostStateStore state, string terminalWorkspace, JsonNode? input, CancellationToken token)
    {
        var edited = EditedPaths(input);
        if (edited.Count == 0) return null;
        var reports = new List<(string Workspace, LanguageResult Result)>();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(Deadline);
        try
        {
            foreach (var group in edited.Select(path => (Path: path, Workspace: CheckoutOf(path, terminalWorkspace)))
                .Where(item => item.Workspace is not null).GroupBy(item => item.Workspace!, StringComparer.Ordinal))
            {
                var paths = group.Select(item => Path.GetRelativePath(group.Key, item.Path)).Distinct(StringComparer.Ordinal).ToArray();
                reports.Add((group.Key, await state.Languages.DiagnoseRunningAsync(group.Key, paths, Budget, bounded.Token)));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return $"tereditor: diagnostics for this edit did not finish within {(int)Deadline.TotalSeconds} s; run lsp_diagnostics with its path.";
        }
        return Format(reports);
    }

    /// <summary>The absolute paths an edit tool's input names: <c>tool_input.file_path</c>, or <c>notebook_path</c> for a notebook.</summary>
    internal static IReadOnlyList<string> EditedPaths(JsonNode? input)
    {
        if (input is not JsonObject root || root["tool_input"] is not JsonObject tool) return [];
        var cwd = root["cwd"] is JsonValue folder && folder.TryGetValue<string>(out var text) ? text : null;
        var paths = new List<string>();
        foreach (var key in new[] { "file_path", "notebook_path" })
        {
            if (tool[key] is not JsonValue value || !value.TryGetValue<string>(out var path) || path.Length == 0) continue;
            if (!Path.IsPathFullyQualified(path))
            {
                if (cwd is null || !Path.IsPathFullyQualified(cwd)) continue;
                path = Path.Combine(cwd, path);
            }
            paths.Add(Path.GetFullPath(path));
        }
        return paths;
    }

    /// <summary>
    /// The checkout whose servers own <paramref name="file"/>: the terminal's own workspace, or another checkout (a worktree)
    /// of the same project, the same rule <c>lsp_diagnostics</c> applies to its <c>workspace</c> argument; else null.
    /// </summary>
    internal static string? CheckoutOf(string file, string terminalWorkspace)
    {
        var calling = Path.TrimEndingDirectorySeparator(Path.GetFullPath(terminalWorkspace));
        if (Inside(file, calling)) return calling;
        if (ProjectServices.KnownCheckout(calling) is not { } origin) return null;
        for (var directory = Path.GetDirectoryName(file); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (!Directory.Exists(Path.Combine(directory, ".git")) && !File.Exists(Path.Combine(directory, ".git"))) continue;
            return ProjectServices.KnownCheckout(directory) is { } target && target.ProjectRoot == origin.ProjectRoot
                ? Path.TrimEndingDirectorySeparator(directory) : null;
        }
        return null;
    }

    private static bool Inside(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// The text the agent reads: one <c>path:line:column severity code message</c> line per error, then per warning (information
    /// and hints are left out), at most <see cref="Cap"/>, then one line per file whose server is not running or that could not
    /// be checked in time. Null when there is none of that.
    /// </summary>
    internal static string? Format(IReadOnlyList<(string Workspace, LanguageResult Result)> reports)
    {
        var shown = reports
            .SelectMany(report => report.Result.Diagnostics.Select(item => (report.Workspace, Item: item, Rank: item.Kind switch { "1" => 1, "2" => 2, _ => 0 })))
            .Where(entry => entry.Rank > 0)
            .OrderBy(entry => entry.Rank).ThenBy(entry => entry.Item.Path, StringComparer.Ordinal).ThenBy(entry => entry.Item.Line).ThenBy(entry => entry.Item.Column)
            .ToArray();
        var notes = new List<string>();
        foreach (var (workspace, result) in reports)
            foreach (var file in result.Files)
            {
                var path = Path.Combine(workspace, file.Path);
                if (file.Freshness == LanguageService.NotRunning)
                    notes.Add($"tereditor: no running language server for {path} ({file.Detail}); skipped. lsp_diagnostics with its path starts it.");
                else if (file.State == "pending")
                    notes.Add($"tereditor: diagnostics for {path} were not ready ({OneLine(file.Detail, MessageLimit)}); run lsp_diagnostics with its path.");
                else if (file.State == "error")
                    notes.Add($"tereditor: {path} could not be checked: {OneLine(file.Detail, MessageLimit)}");
            }
        if (shown.Length == 0 && notes.Count == 0) return null;
        var text = new StringBuilder();
        if (shown.Length > 0)
        {
            text.Append("tereditor: diagnostics from the running language server after this edit (line:column 1-based; lsp_* positions are zero-based):\n");
            foreach (var (workspace, item, rank) in shown.Take(Cap))
            {
                text.Append(Path.Combine(workspace, item.Path)).Append(':').Append(item.Line + 1).Append(':').Append(item.Column + 1)
                    .Append(rank == 1 ? " error" : " warning");
                if (Code(item) is { Length: > 0 } code) text.Append(' ').Append(code);
                text.Append(' ').Append(OneLine(item.Text, MessageLimit)).Append('\n');
            }
            if (shown.Length > Cap) text.Append("… and ").Append(shown.Length - Cap).Append(" more; lsp_diagnostics with the path lists them all.\n");
        }
        foreach (var note in notes) text.Append(note).Append('\n');
        return text.ToString().TrimEnd('\n');
    }

    private static string Code(LanguageItem item)
    {
        if (item.Raw.Length == 0) return "";
        try { return JsonNode.Parse(item.Raw)?["code"] is JsonValue code ? OneLine(code.ToString(), 60) : ""; }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    private static string OneLine(string text, int limit)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= limit ? line : line[..(limit - 1)] + "…";
    }
}