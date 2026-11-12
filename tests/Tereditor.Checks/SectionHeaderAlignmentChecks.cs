using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Host.Core;
using Tereditor.UI.Panels;

using Path = System.IO.Path;

namespace Tereditor.Checks;

/// <summary>Under the application's real styles every collapsible section header starts at the same left edge as the content under it: the Code Intelligence Running section, the shared expander and a link-style header.</summary>
internal static class SectionHeaderAlignmentChecks
{
    private static double Left(Visual visual, Visual root) => visual.TranslatePoint(default, root)?.X ?? throw new InvalidOperationException("A control is not in the window.");

    private static void Aligned(string what, double header, double content)
    {
        if (Math.Abs(header - content) > 0.5) throw new InvalidOperationException($"{what}: header left edge {header:0.0} differs from content left edge {content:0.0}.");
    }

    internal static void RunUi(string fixture)
    {
        var root = Path.Combine(fixture, "section-headers"); Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, ".tools", "state"); Directory.CreateDirectory(statePath);
        var state = new HostStateStore(statePath);
        var running = new LanguageRunningView(new LocalProjectAdapter(new ProjectServices(root, state)), root);
        var content = new TextBlock { Text = "Content under the expander" };
        var expander = new Expander { Header = "Expander header", IsExpanded = true, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        var link = new Button { Content = "Link header", Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Left };
        var below = new TextBlock { Text = "Text under the link" };
        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 6, Children = { running, expander, link, below } };
        var window = new Window { Width = 360, Height = 480, Content = stack };
        window.Show();
        try
        {
            E2E.E2eWorkspace.Until(() => (running.GetVisualDescendants().OfType<Button>().First().Content as string)?.Contains("Reading") == false, "The Running section did not settle.");
            running.Show(new LanguageResult("ok") { Servers = [new("csharp", "x", "running", "idle", "1") { Workspace = root, MemoryBytes = 28 << 20 }] });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var header = running.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("link"));
            var title = running.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "RunningServers").Children.OfType<TextBlock>().First();
            Aligned("Running section", Left(header.GetVisualDescendants().OfType<TextBlock>().First(), window), Left(title, window));
            var chevron = expander.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(path => path.Classes.Contains("expander-chevron"));
            Aligned("Expander", Left(chevron, window), Left(content, window));
            Aligned("Link header", Left(link.GetVisualDescendants().OfType<TextBlock>().First(), window), Left(below, window));
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Section headers: the Running section, the expander and link headers start at their content's left edge");
    }
}