using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.State;

namespace SharpRail.Checks;

/// <summary>The Code Intelligence pane and the shared pane header from very narrow to wide: names never break inside a word, actions stay reachable and titles never run under their buttons.</summary>
internal static class LanguagePaneNarrowChecks
{
    private static readonly double[] Widths = [160, 220, 320, 480];

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static void Run(string fixture)
    {
        Header(fixture);
        Rows(fixture);
    }

    private static void Rows(string fixture)
    {
        var root = Path.Combine(fixture, "language-narrow"); Directory.CreateDirectory(Path.Combine(root, "scripts"));
        var statePath = Path.Combine(root, ".tools", "state"); Directory.CreateDirectory(statePath);
        File.WriteAllText(Path.Combine(root, "scripts", "window-probe.m"), "int main(void) { return 0; }");
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class Program {}");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = new HostStateStore(statePath);
        using var shared = new SharedState(new LocalStateAdapter(state), new(), state.Current);
        shared.Start();
        var pane = new SharpRail.UI.Panels.LanguagePanel(new LocalProjectAdapter(new ProjectServices(root, state)), root, _ => Task.CompletedTask, shared);
        var window = new Window { Width = 480, Height = 900, Content = pane };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            var servers = pane.FindControl<ListBox>("Servers")!;
            E2E.E2eWorkspace.Until(() => servers.Items.Any(item => item?.ToString() == "C / C++ (clangd)") && servers.Items.Any(item => item?.ToString() == "C# (Roslyn)"),
                "The narrow-layout fixture did not list clangd and Roslyn.");
            foreach (var width in Widths)
            {
                window.Width = width;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                for (var index = 0; index < servers.ItemCount; index++)
                {
                    var row = (ListBoxItem)servers.ContainerFromIndex(index)!;
                    var name = row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerTitle");
                    Require(!BreaksInsideWord(name), $"At {width}px the server name '{name.Text}' broke inside a word.");
                    foreach (var text in row.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text)))
                    {
                        var longest = text.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(word => Measure(text, word));
                        Require(text.Bounds.Width + 1 >= Math.Min(longest, row.Bounds.Width * 0.6),
                            $"At {width}px '{text.Text}' was laid out {text.Bounds.Width:0}px wide, narrower than its longest word ({longest:0}px).");
                    }
                    foreach (var action in new Control[] { row.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerSettingsAction"),
                        row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerDisclosure") })
                    {
                        Require(action.IsEffectivelyVisible && action.IsEnabled, $"At {width}px a server row action was hidden.");
                        var center = action.TranslatePoint(new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), row)!.Value;
                        Require(center.X > 0 && center.X < row.Bounds.Width && center.Y > 0 && center.Y < row.Bounds.Height,
                            $"At {width}px a server row action {action.Name} was outside its row (center {center}, row {row.Bounds.Size}, grid row {Grid.GetRow((Control)action.GetVisualParent()!)}, surface {row.GetLogicalDescendants().OfType<Border>().First(b => b.Name == "ServerRowSurface").Bounds}).");
                    }
                }
                LanguageChecks.CaptureLsp(window, $"narrow-{width}");
            }
            Console.WriteLine("PASS Code Intelligence rows at 160-480 px keep names whole, details full-width and actions reachable");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled); }
    }

    private static void Header(string fixture)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "language-narrow-header"), openFiles: false);
        app.Click(app.Find<Button>("RailUnplaced_lsp"));
        E2E.E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "LspPanel" && control.IsEffectivelyVisible));
        var headers = app.Window.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name?.StartsWith("GroupHeader_", StringComparison.Ordinal) == true && grid.IsEffectivelyVisible &&
            grid.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "PaneTitle")).ToArray();
        Require(headers.Length > 0, "No pane header was found.");
        foreach (var header in headers)
        {
            var frame = (Control)header.Parent!;
            foreach (var width in Widths)
            {
                frame.Width = width;
                app.Window.UpdateLayout();
                var title = header.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "PaneTitle");
                var actions = (Control)header.Children[1];
                var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), header)!.Value.X;
                var actionsLeft = actions.TranslatePoint(default, header)!.Value.X;
                Require(titleRight <= actionsLeft + 0.5, $"At {width}px the pane title '{title.Text}' ran under its buttons ({titleRight:0} > {actionsLeft:0}).");
            }
            frame.Width = double.NaN;
        }
        Console.WriteLine("PASS pane headers trim titles before their buttons at 160-480 px");
    }

    private static double Measure(TextBlock source, string text)
    {
        var probe = new TextBlock { Text = text, FontSize = source.FontSize, FontFamily = source.FontFamily, FontWeight = source.FontWeight };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    // A wrapped line that ends mid-word (neither side of the break is a space) means the name was broken inside a word.
    private static bool BreaksInsideWord(TextBlock text)
    {
        var value = text.Text ?? "";
        var lines = text.TextLayout.TextLines;
        for (var line = 0; line < lines.Count - 1; line++)
        {
            var end = lines[line].FirstTextSourceIndex + lines[line].Length;
            if (end > 0 && end < value.Length && !char.IsWhiteSpace(value[end - 1]) && !char.IsWhiteSpace(value[end])) return true;
        }
        return false;
    }
}