using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Markdig.Syntax;

using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Checks;

internal static class MarkdownMermaidDiffChecks
{
    internal static void Run()
    {
        const string before = "```mermaid\ngraph TD\n A-->B\n```\n";
        const string after = "```mermaid\ngraph TD\n A-->C\n C-->D\n```\n";
        Check(before, before, 1, []);
        Check("", "```mermaid\nbroken nonsense\n```\n", 1, ["Added"], invalid: true);
        Check(before, after, 2, ["Before", "After"]);
        Check(before[..^4], after[..^4], 2, ["Before", "After"]);
        Check("", after, 1, ["Added"]);
        Check(before, "", 1, ["Removed"]);
        Check(before, "replacement paragraph\n", 1, ["Removed"]);
        Check("previous paragraph\n", after, 1, ["Added"]);
        Check(before + "\n" + before, before + "\n" + after, 3, ["Before", "After"]);
        Check("- " + before.Replace("\n", "\n  ", StringComparison.Ordinal), "- " + after.Replace("\n", "\n  ", StringComparison.Ordinal), 2, ["Before", "After"]);
        var codeBefore = before.Replace("mermaid", "text", StringComparison.Ordinal);
        var codeAfter = after.Replace("mermaid", "text", StringComparison.Ordinal);
        Require(MarkdownDiff.Merge(codeBefore, codeAfter) == codeAfter, "Changed ordinary fenced code retains modified source degradation.");
        Require(MarkdownPreview.Parse(MarkdownDiff.Merge(before, codeAfter)).Descendants<FencedCodeBlock>().Count() == 2 && MarkdownPreview.Parse(MarkdownDiff.Merge(codeBefore, after)).Descendants<FencedCodeBlock>().Count() == 2, "Mermaid/code conversion preserves both complete fenced versions.");
        Check(string.Join('\n', before.Split('\n').Select(line => "> " + line)), string.Join('\n', after.Split('\n').Select(line => "> " + line)), 2, ["Before", "After"]);
        Check(before.Replace("```", "~~~~", StringComparison.Ordinal), after.Replace("```", "~~~~", StringComparison.Ordinal), 2, ["Before", "After"]);
        var sources = new List<((int Start, int End)? Old, (int Start, int End)? New)>();
        var merged = MarkdownDiff.MergeWithSourceLines(before, after, (oldLines, newLines) => sources.Add((oldLines, newLines)));
        Require(sources.Count == merged.Count(character => character == '\n'), "Every emitted diagram/label line retains source mapping.");
        Require(sources.Any(span => span.Old == (1, 4) && span.New is null) && sources.Any(span => span.Old is null && span.New == (1, 5)), "Before and after diagram source bounds retain their own side.");
        var prose = MarkdownDiff.Merge("old paragraph\n\n" + before + "\ntail\n", "new paragraph\n\n" + after + "\ntail\n");
        Require(prose.Contains("<del>old</del><ins>new</ins>", StringComparison.Ordinal) && prose.EndsWith("tail\n", StringComparison.Ordinal), "Diagram merging preserves surrounding prose marks.");
        Console.WriteLine("PASS Markdown Mermaid diffs: unchanged, before/after, added/removed, tilde fences, source bounds and renderer fallback");
    }

    private static void Check(string before, string after, int count, string[] labels, bool invalid = false)
    {
        var merged = MarkdownDiff.Merge(before, after);
        var document = MarkdownPreview.Parse(merged);
        var fences = document.Descendants<FencedCodeBlock>().ToArray();
        Require(fences.Length == count && fences.All(block => block.Info == "mermaid"), "Merged diagrams remain complete Mermaid fences: " + merged);
        Require(fences.All(block => !block.Lines.ToString().Contains("<ins>", StringComparison.Ordinal) && !block.Lines.ToString().Contains("<del>", StringComparison.Ordinal)), "Diagram syntax receives no word diff tags.");
        Require(labels.All(label => merged.Contains("**" + label + "**", StringComparison.Ordinal)), "Diagram versions carry readable labels.");
        if (before == after) Require(merged == after, "Unchanged diagrams remain unchanged and render once.");
        var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 100, true);
        using var preview = new MarkdownPreview(document, "README.md", context);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int Rendered() => preview.GetLogicalDescendants().OfType<Control>().Count(control => control.Name is "MermaidInline" or "MermaidError");
        while (Rendered() != count && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Require(Rendered() == count, "Every diagram reaches the existing renderer or its error/unavailability fallback.");
        if (OperatingSystem.IsMacOS())
            Require(preview.GetLogicalDescendants().OfType<Control>().Count(control => control.Name == (invalid ? "MermaidError" : "MermaidInline")) == count, "Valid diagrams render through Merman; invalid diagrams retain its source/error fallback.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}