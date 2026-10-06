using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;

using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class MarkdownAlertsE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "markdown-alerts"));
        app.Open("ALERTS.md", true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        var alerts = preview.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "MarkdownAlert").ToArray();
        Require(alerts.Length == 5, "Markdown must render five distinct callouts.");
        foreach (var variant in new[] { "note", "tip", "important", "warning", "caution" })
        {
            var alert = alerts.Single(alert => Equals(alert.Tag, variant));
            Require(alert.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == char.ToUpperInvariant(variant[0]) + variant[1..]),
                "The callout must display its upstream label: " + variant);
        }
        var rendered = string.Join('\n', preview.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Select(text => string.Concat(text.Inlines?.OfType<Run>().Select(run => run.Text) ?? [])));
        Require(rendered.Contains("Useful information", StringComparison.Ordinal) && !rendered.Contains("[!NOTE]", StringComparison.Ordinal),
            "The rendered note must preserve content and hide its source marker.");
        app.Click(app.Find<Button>("MarkdownSourceMode"));
        var source = app.Find<Control>("MarkdownSource");
        Require(MarkdownSourceText(source).Contains("[!NOTE]", StringComparison.Ordinal),
            "Source mode must display the original alert marker.");
        app.Click(app.Find<Button>("MarkdownPreviewMode"));
        Require(ReferenceEquals(preview, app.Find<MarkdownPreview>("MarkdownPreview")), "Returning to preview must retain its mounted document.");
        Console.WriteLine("PASS upstream markdown-alerts.spec.ts: renders GitHub-style alert callouts in the rendered markdown view");
    }
}