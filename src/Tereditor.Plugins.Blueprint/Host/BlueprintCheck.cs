using System.ComponentModel;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Blueprint.Host;

internal sealed record BlueprintCheckParams
{
    [Description("Return the full Blueprint format reference instead of checking the file. Read it before writing BLUEPRINT.md.")]
    public bool Reference { get; init; }
}

internal static class BlueprintCheck
{
    public const string Description = "Read back the interactive specification exactly as the reader's panel renders it: every control, its kind, how many options it has, what is selected, and every place the parser had to decide something for you — a kind word it did not know, an id it invented or renamed, an option with no reason after it. Call this after writing or rewriting BLUEPRINT.md. It reads the file and changes nothing. The format in brief: YAML frontmatter (id, type, status, title, tags), Markdown, and decisions as control blocks — a line `!control select <id>` or `!control multi <id>` followed by options, `= ` marks the selected option of a select, `[x]`/`[ ]` the checked ones of a multi, each option written `label — reason`; ids are stable kebab-case names of the question. Pass reference=true for the full format reference.";

    /// <summary>The smallest valid document, shown when a file renders no controls. A check proves it parses to controls without notes.</summary>
    public const string MinimalExample = """
        ---
        id: demo-spec
        type: goal-and-requirements
        status: draft
        title: Demo
        ---
        ## Goal

        A tool.

        !control select database
        = SQLite — zero setup
        - Postgres — concurrent writers
        """;

    public static PluginToolResult Run(string path, bool reference = false)
    {
        if (reference) return new(BlueprintPrompts.FormatDocument);
        var text = BlueprintSessions.ReadFile(path);
        if (text is null) return new($"There is no {BlueprintContract.File} at the root of this directory yet — write the file, then check it.") { IsError = true };
        var parsed = BlueprintFormat.Read(text);
        var controls = BlueprintFormat.Controls(parsed.Doc);
        var lines = new List<string> { $"{BlueprintContract.File} — {Count(controls.Count, "control")}, {Count(parsed.Notes.Count, "note")}." };
        if (controls.Count == 0)
            lines.AddRange(["", $"No control was found: a control is a line `!control select <id>` or `!control multi <id>` on its own, followed by its option lines. A minimal valid {BlueprintContract.File}:", "", "```markdown", MinimalExample, "```", "", "Call blueprint_check with reference=true for the full format reference."]);
        else
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

[JsonSerializable(typeof(BlueprintCheckParams))]
[JsonSerializable(typeof(Dictionary<string, PersistedBlueprint>))]
internal sealed partial class BlueprintHostJson : JsonSerializerContext;