using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Blueprint.Host;

internal sealed record BlueprintCheckParams;

internal static class BlueprintCheck
{
    public const string Description = "Read back the interactive specification exactly as the reader's panel renders it: every control, its kind, how many options it has, what is selected, and every place the parser had to decide something for you — a kind word it did not know, an id it invented or renamed, an option with no reason after it. Call this after writing or rewriting BLUEPRINT.md. It reads the file and changes nothing.";

    public static PluginToolResult Run(string path)
    {
        var text = BlueprintSessions.ReadFile(path);
        if (text is null) return new($"There is no {BlueprintContract.File} at the root of this directory yet — write the file, then check it.") { IsError = true };
        var parsed = BlueprintFormat.Read(text);
        var controls = BlueprintFormat.Controls(parsed.Doc);
        var lines = new List<string> { $"{BlueprintContract.File} — {Count(controls.Count, "control")}, {Count(parsed.Notes.Count, "note")}." };
        if (controls.Count > 0)
        {
            lines.Add("");
            lines.AddRange(controls.Select(control => $"{control.Id} ({control.Kind.ToString().ToLowerInvariant()}) — {Count(control.Options.Count, "option")}, {BlueprintFormat.SelectedLabels(control)} selected"));
        }
        if (parsed.Notes.Count > 0)
        {
            lines.AddRange(["", "What the panel read differently from what you wrote:"]);
            lines.AddRange(parsed.Notes.Select(note => $"- {note.Control}: {note.Message}"));
            lines.AddRange(["", $"Fix these in {BlueprintContract.File} and check again."]);
        }
        return new(string.Join('\n', lines));
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}