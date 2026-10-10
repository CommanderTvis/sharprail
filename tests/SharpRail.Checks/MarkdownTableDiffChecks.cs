using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;

using Markdig.Extensions.Tables;
using Markdig.Syntax;

using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Checks;

internal static class MarkdownTableDiffChecks
{
    private const string Table = "| Plugin | Purpose |\n| :--- | ---: |\n| Codex | Edit code |\n| Specs | Read specs |\n";

    internal static void Run()
    {
        Check("", Table, 1, 3, "ins", "Codex");
        Check(Table, "", 1, 3, "del", "Codex");
        Check(Table, Table.Replace("Edit code", "Review code", StringComparison.Ordinal), 1, 3, "ins", "Review");
        Check(Table, Table.Replace("Edit code", "Review code", StringComparison.Ordinal), 1, 3, "del", "Edit");
        Check(Table, Table + "| PDF | Read PDFs |\n", 1, 4, "ins", "PDF");
        Check(Table, Table.Replace("| Specs | Read specs |\n", "", StringComparison.Ordinal), 1, 3, "del", "Specs");
        Check(Table, Table.Replace("Purpose", "Use", StringComparison.Ordinal), 1, 3, "ins", "Use");
        Check("", "Name | Value\n--- | ---\nx | y\n", 1, 2, "ins", "x");
        Check("", "| Name | Value |\n| --- | --- |\n| a\\|b | `code` |\n", 1, 2, "ins", "a|b");
        Check("", "| Name | Value |\n| --- | --- |\n", 1, 1, "ins", "Name");
        Check("", "| Name | Value |\n| --- | --- |\n| | filled |\n", 1, 2, "ins", "filled");
        Check(Table, Table.Replace("| Codex | Edit code |", "| Codex |", StringComparison.Ordinal), 1, 4, "del", "Edit code");
        Check("", "> | Name | Value |\n> | --- | --- |\n> | x | y |\n", 1, 2, "ins", "x");
        Check("", "---\ntitle: A | B\n---\n\n" + Table, 1, 3, "ins", "Codex");
        Check(Table, "| Plugin | Purpose | Extra |\n| --- | --- | --- |\n| Codex | Edit code | New |\n", 2, 5, "ins", "New");
        var same = MarkdownDiff.Merge(Table, Table);
        Require(same == Table, "An unchanged table keeps its source exactly.");
        var code = MarkdownDiff.Merge("", "```\n" + Table + "```\n");
        Require(!MarkdownPreview.Parse(code).Descendants<Table>().Any(), "Table-like fenced code remains code.");
        Require(!MarkdownPreview.Parse(MarkdownDiff.Merge("", "ordinary | prose\n")).Descendants<Table>().Any(), "A pipe in prose does not become a table.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { MarkdownDiff.Merge(Table, Table, cancelled.Token); throw new InvalidOperationException("A cancelled table merge must stop."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS Markdown table diffs: added/removed tables, cell/header edits, rows, column changes, syntax and native cell marks");
    }

    private static void Check(string before, string after, int tableCount, int rowCount, string mark, string markedText)
    {
        var merged = MarkdownDiff.Merge(before, after);
        var document = MarkdownPreview.Parse(merged);
        var tables = document.Descendants<Table>().ToArray();
        Require(tables.Length == tableCount && tables.Sum(table => table.Count) == rowCount,
            "The merged Markdown must preserve table structure: " + merged);
        Require(tables.SelectMany(table => table.OfType<TableRow>()).All(row => row.Count >= 2), "Rows keep separate cells.");
        var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 100, true);
        using var preview = new MarkdownPreview(document, "README.md", context, renderDiagrams: false);
        var grids = preview.GetLogicalDescendants().OfType<Grid>().Where(grid => grid.ColumnDefinitions.Count >= 2).ToArray();
        Require(grids.Length == tableCount && grids.Sum(grid => grid.RowDefinitions.Count) == rowCount, "The native preview draws table grids.");
        var marks = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().SelectMany(text => text.Inlines?.OfType<Run>() ?? [])
            .Where(run => run.Classes.Contains(mark)).Select(run => run.Text);
        Require(string.Concat(marks).Contains(markedText, StringComparison.Ordinal), "Cell text retains its insertion/deletion mark: " + merged);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}