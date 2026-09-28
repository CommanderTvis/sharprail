using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using SharpRail.UI.Rendering;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class MarkdownLinksE2E
{
    private static Button Link(E2eWorkspace app, string title) => app.Find<MarkdownPreview>("MarkdownPreview")
        .GetLogicalDescendants().OfType<SelectableTextBlock>()
        .SelectMany(text => text.Inlines?.OfType<InlineUIContainer>() ?? [])
        .Select(inline => inline.Child).OfType<Button>()
        .Single(button => button.Content is TextBlock label && label.Text == title);

    internal static void Run(string root)
    {
        using (var app = new E2eWorkspace(Path.Combine(root, "markdown-parent-link")))
        {
            app.ExpandFolder("styles"); app.Open("styles/COLOR.md", true);
            app.Open("LINKS.md"); app.Click(app.Tab("styles/COLOR.md"));
            Until(() => app.Window.Layout.Selected(app.Center)?.Path == "styles/COLOR.md");
            app.Click(Link(app, "themes/SPEC.md"));
            Until(() => app.Window.Layout.Selected(app.Center)?.Path == "themes/SPEC.md");
            Require(app.Tabs.Count == 2 && app.Tabs.Any(tab => tab.Path == "styles/COLOR.md" && !tab.Preview) &&
                app.Tabs.Any(tab => tab.Path == "themes/SPEC.md" && tab.Preview) && app.Tabs.All(tab => tab.Path != "LINKS.md"),
                "Parent-relative links must navigate inside the workspace and replace the preview slot.");
            Require(app.Find<MarkdownPreview>("MarkdownPreview").GetLogicalDescendants().OfType<SelectableTextBlock>()
                .Any(text => text.Inlines?.OfType<Run>().Any(run => run.Text == "Theme spec target") == true),
                "The parent-relative link must render its target document.");
            Console.WriteLine("PASS upstream markdown-links.spec.ts: a parent-relative file link cannot escape into browser navigation");
        }
        using (var app = new E2eWorkspace(Path.Combine(root, "markdown-relative-link")))
        {
            app.Open("LINKS.md", true);
            var preview = app.Find<MarkdownPreview>("MarkdownPreview");
            var images = preview.GetLogicalDescendants().OfType<SelectableTextBlock>()
                .SelectMany(text => text.Inlines?.OfType<InlineUIContainer>() ?? [])
                .Select(inline => inline.Child).OfType<Border>().ToArray();
            Require(images.Length == 1, "The Markdown image must have one native inline container.");
            Until(() => images[0].Child is Image image && image.Source is Bitmap bitmap && bitmap.PixelSize.Width > 0);
            Require(app.Host.Reads.GetValueOrDefault("logo.png") == 1, "The relative image must resolve through the workspace host.");
            app.Click(Link(app, "Section two")); Settle();
            Require(app.Tabs.Count == 1 && ReferenceEquals(preview, app.Find<MarkdownPreview>("MarkdownPreview")),
                "An in-document anchor must preserve the preview and tab count.");
            app.Click(Link(app, "the spec")); Until(() => app.Window.Layout.Selected(app.Center)?.Path == "SPEC.md");
            Require(app.Tabs.Count == 2 && app.Tabs.Single(tab => tab.Path == "SPEC.md").Preview,
                "A relative file link must open a preview beside the kept document.");
            Console.WriteLine("PASS upstream markdown-links.spec.ts: relative links, images, and heading anchors work in the rendered markdown view");
        }
    }
}
