using System.Text;

using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private const string ProposedDiffKind = "proposed-diff";
    private readonly Dictionary<string, string> proposedDiffs = [];

    /// <summary>Opens a read-only diff of a workspace file against text that is not on disk yet (Plugin API W27).</summary>
    internal async Task OpenProposedDiffAsync(string workspace, string path, string newContent)
    {
        if (!WorkspaceMounted || atHome || workspace != workspaceRoot) return;
        var relative = path.StartsWith(workspaceRoot + "/", StringComparison.Ordinal) ? path[(workspaceRoot.Length + 1)..] : path;
        string current;
        try { current = (await host.ReadFileAsync(relative, lifetime.Token)).Text; }
        catch (Exception error) when (error is not OperationCanceledException) { current = ""; }
        var diff = await Task.Run(() => TextDiff.Unified(relative, current, newContent), lifetime.Token);
        if (!WorkspaceMounted || atHome || workspace != workspaceRoot) return;
        var id = "proposed:" + Guid.NewGuid().ToString("N")[..12];
        proposedDiffs[workspaceRoot + ":" + id] = diff;
        Layout.Open(new(id, relative + " (proposed)", ProposedDiffKind, relative), keep: true);
    }

    // Nothing is persisted for a proposal, so a restored tab only says so.
    private Avalonia.Controls.Control ProposedDiffContent(Docking.DockTab tab, string key)
    {
        if (!proposedDiffs.TryGetValue(key, out var diff)) return Ui.Text("This proposed change is no longer available.");
        var view = new DiffView(diff, tab.Path, LineWidths.File(Preferences.FileLineWidth, Preferences.FileLineWidthBounded));
        documentContent[key] = view;
        return view;
    }
}

/// <summary>A unified diff of two texts, line by line, for views that read Git's format.</summary>
internal static class TextDiff
{
    private const int Context = 3;
    private const int Limit = 4000;

    public static string Unified(string path, string before, string after)
    {
        var old = Lines(before);
        var next = Lines(after);
        var builder = new StringBuilder();
        builder.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
        builder.Append("--- a/").Append(path).Append('\n').Append("+++ b/").Append(path).Append('\n');
        var script = old.Length > Limit || next.Length > Limit ? Replace(old, next) : Script(old, next);
        for (var start = 0; start < script.Count;)
        {
            while (start < script.Count && script[start].Kind == ' ') start++;
            if (start >= script.Count) break;
            var first = Math.Max(0, start - Context);
            var end = start;
            var lastChange = start;
            while (end < script.Count && end - lastChange <= 2 * Context) { if (script[end].Kind != ' ') lastChange = end; end++; }
            var stop = Math.Min(script.Count, lastChange + Context + 1);
            var oldStart = script.Take(first).Count(line => line.Kind != '+') + 1;
            var newStart = script.Take(first).Count(line => line.Kind != '-') + 1;
            var hunk = script.Skip(first).Take(stop - first).ToArray();
            builder.Append($"@@ -{oldStart},{hunk.Count(line => line.Kind != '+')} +{newStart},{hunk.Count(line => line.Kind != '-')} @@\n");
            foreach (var line in hunk) builder.Append(line.Kind).Append(line.Text).Append('\n');
            start = stop;
        }
        return builder.ToString();
    }

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');

    private static List<(char Kind, string Text)> Replace(string[] old, string[] next) =>
        [.. old.Select(line => ('-', line)), .. next.Select(line => ('+', line))];

    private static List<(char Kind, string Text)> Script(string[] old, string[] next)
    {
        var table = new int[old.Length + 1, next.Length + 1];
        for (var i = old.Length - 1; i >= 0; i--)
            for (var j = next.Length - 1; j >= 0; j--)
                table[i, j] = old[i] == next[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        var script = new List<(char, string)>();
        int a = 0, b = 0;
        while (a < old.Length && b < next.Length)
        {
            if (old[a] == next[b]) { script.Add((' ', old[a])); a++; b++; }
            else if (table[a + 1, b] >= table[a, b + 1]) script.Add(('-', old[a++]));
            else script.Add(('+', next[b++]));
        }
        while (a < old.Length) script.Add(('-', old[a++]));
        while (b < next.Length) script.Add(('+', next[b++]));
        return script;
    }
}