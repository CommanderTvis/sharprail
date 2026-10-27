using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
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
        Churn(fixture);
        Hidden(fixture);
    }

    /// <summary>Hide removes a suggested row behind "1 hidden · Show", persists per project in host state, stops suggesting it, and Show/Unhide bring it back.</summary>
    private static void Hidden(string fixture)
    {
        var root = Path.Combine(fixture, "language-hidden"); Directory.CreateDirectory(Path.Combine(root, "scripts"));
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
            const string clangd = "C / C++ (clangd)";
            var servers = pane.FindControl<ListBox>("Servers")!;
            var toggle = pane.FindControl<Button>("HiddenToggle")!;
            ListBoxItem? Row(string name) => Enumerable.Range(0, servers.ItemCount).Where(index => servers.Items[index]?.ToString() == name)
                .Select(index => servers.ContainerFromIndex(index) as ListBoxItem).FirstOrDefault();
            void Click(ListBoxItem row, string name) => row.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == name && button.IsEffectivelyVisible)
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => Row(clangd) is not null && !toggle.IsVisible, "The hidden-servers fixture did not suggest clangd.");
            for (var index = 0; index < servers.ItemCount; index++)
            {
                var needs = ((ListBoxItem)servers.ContainerFromIndex(index)!).GetLogicalDescendants().OfType<TextBlock>()
                    .Count(text => text.IsEffectivelyVisible && (text.Text ?? "").StartsWith("Needs ", StringComparison.Ordinal));
                Require(needs <= 1, $"'{servers.Items[index]}' showed Needs {needs} times.");
            }
            Click(Row(clangd)!, "ServerHideAction");
            E2E.E2eWorkspace.Until(() => Row(clangd) is null && toggle.IsVisible && toggle.Content as string == "1 hidden · Show", "Hide did not remove clangd behind 1 hidden · Show.");
            Require(state.Current.ProjectLanguageHidden.GetValueOrDefault(root) == "[\"clangd\"]", "Hide did not reach host state for the project.");
            Require(new HostStateStore(statePath).Current.ProjectLanguageHidden.GetValueOrDefault(root) == "[\"clangd\"]", "The hidden server did not survive reload.");
            Require(!(pane.FindControl<ComboBox>("AddServer")!.ItemsSource?.Cast<string>() ?? []).Contains("clangd"), "A hidden server was still offered.");
            Require(Row("C# (Roslyn)") is not null, "Hiding clangd hid another server.");
            Console.WriteLine("PASS Hide removes a suggested server behind 1 hidden · Show, persisted per project, and stops suggesting it");
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => Row(clangd) is { } row && row.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "ServerUnhideAction" && button.IsEffectivelyVisible),
                "Show did not reveal the hidden server with Unhide.");
            Click(Row(clangd)!, "ServerUnhideAction");
            E2E.E2eWorkspace.Until(() => Row(clangd) is not null && !toggle.IsVisible && !state.Current.ProjectLanguageHidden.ContainsKey(root), "Unhide did not bring clangd back.");
            Console.WriteLine("PASS Show reveals hidden servers and Unhide restores them");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled); }
    }

    /// <summary>Refreshing ~20 servers 50 times keeps one row per server: rows from the first refreshes are collected, so no scope or subscription retains them.</summary>
    private static void Churn(string fixture)
    {
        var root = Path.Combine(fixture, "language-churn"); Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, ".tools", "state"); Directory.CreateDirectory(statePath);
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = new HostStateStore(statePath);
        using var shared = new SharedState(new LocalStateAdapter(state), new(), state.Current);
        shared.Start();
        var pane = new SharpRail.UI.Panels.LanguagePanel(new LocalProjectAdapter(new ProjectServices(root, state)), root, _ => Task.CompletedTask, shared);
        var window = new Window { Width = 320, Height = 900, Content = pane };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            var servers = pane.FindControl<ListBox>("Servers")!;
            const int count = 20;
            var early = new List<WeakReference>();
            for (var round = 0; round < 50; round++)
            {
                pane.ShowInventory(Enumerable.Range(0, count).Select(index => new LanguageServerState($"fake-{index}", "file.x", round % 2 == 0 ? "ready" : "error", $"round {round}", "1")).ToArray());
                Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                if (round < 3) early.AddRange(servers.Items.Cast<object>().Select(item => new WeakReference(item)));
            }
            // The pane also asks the host for its real inventory when it opens; that reply may land mid-churn and replace the fake one
            // (the host is the truth), so settle that request and show the final inventory again before counting.
            E2E.E2eWorkspace.Until(() => pane.FindControl<Button>("Refresh")!.IsEnabled, "The pane's own inventory request did not finish.");
            pane.ShowInventory(Enumerable.Range(0, count).Select(index => new LanguageServerState($"fake-{index}", "file.x", "ready", "final", "1")).ToArray());
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Require(servers.ItemCount == count, $"The pane showed {servers.ItemCount} rows for {count} servers.");
            for (var pass = 0; pass < 2; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            var alive = early.Count(reference => reference.IsAlive);
            Require(alive == 0, $"{alive} of {early.Count} server rows from early refreshes were still reachable after 50 refreshes.");
            Console.WriteLine("PASS 50 refreshes of 20 servers leave one row per server and release replaced rows");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled); }
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
                double? settingsLeft = null, disclosureLeft = null;
                for (var index = 0; index < servers.ItemCount; index++)
                {
                    var row = (ListBoxItem)servers.ContainerFromIndex(index)!;
                    var name = row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerTitle");
                    Require(name.TextLayout.TextLines.Count == 1, $"At {width}px the server name '{name.Text}' wrapped instead of trimming.");
                    var settings = row.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerSettingsAction");
                    var disclosure = row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerDisclosure");
                    var icon = row.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "ServerLanguageIcon");
                    var status = row.GetLogicalDescendants().OfType<Control>().First(control => control.Name is "ServerStatusIcon" or "ServerBootSpinner" && control.IsEffectivelyVisible);
                    var nameCenter = CenterY(name, row);
                    foreach (var part in new Control[] { status, icon, settings, disclosure })
                        Require(Math.Abs(CenterY(part, row) - nameCenter) <= 3, $"At {width}px {part.Name} left the header line of '{name.Text}' ({CenterY(part, row):0} vs {nameCenter:0}).");
                    var left = settings.TranslatePoint(default, row)!.Value.X;
                    var actionLeft = disclosure.TranslatePoint(default, row)!.Value.X;
                    settingsLeft ??= left;
                    disclosureLeft ??= actionLeft;
                    Require(Math.Abs(left - settingsLeft.Value) <= 0.5 && Math.Abs(actionLeft - disclosureLeft.Value) <= 0.5,
                        $"At {width}px the action columns of '{name.Text}' did not align with the first row (settings at {left:0} vs {settingsLeft:0}).");
                    foreach (var text in row.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.TextWrapping != Avalonia.Media.TextWrapping.NoWrap && text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text)))
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
            Console.WriteLine("PASS Code Intelligence rows at 160-480 px keep one aligned header line, trimmed names and reachable actions");
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

    private static double CenterY(Control control, Visual row) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), row)!.Value.Y;

    private static double Measure(TextBlock source, string text)
    {
        var probe = new TextBlock { Text = text, FontSize = source.FontSize, FontFamily = source.FontFamily, FontWeight = source.FontWeight };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }
}