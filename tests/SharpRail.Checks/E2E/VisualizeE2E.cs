using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

// Translates the fork's e2e/plugins/visualize/visualize.spec.ts terminal scenario. The fork drives the terminal's
// MCP address with curl; the remote case uses the HTTP address emitted by its real host PTY. The local case
// calls the embedded tool table, while both render the companion with real headless input.
internal static class VisualizeE2E
{
    internal static void Run(string root)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP visualize companion: Mermaid rendering requires macOS"); return; }
        foreach (var remote in new[] { false, true })
        {
            var workspace = Path.Combine(root, "visualize", remote ? "remote" : "local");
            Directory.CreateDirectory(workspace);
            var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "visualize-ui", workspace + "-state") : null;
            try
            {
                if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
                var endpoint = server is null ? null : new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var terminalAdapter = endpoint is null ? null : new RemoteTerminalAdapter(endpoint, "visualize-ui");
                using var app = server is null ? new E2eWorkspace(workspace)
                    : new E2eWorkspace(endpoint!, "visualize-ui", workspace, workspace + "-profile", workspace, terminals: new E2eTerminals(terminalAdapter!));
                Until(() => app.Window.WorkspaceMounted);
                Scenario(app, server?.Services.GetRequiredService<PluginRuntime>() ?? app.PluginRuntime!, remote);
                Console.WriteLine($"PASS visualize {(remote ? "remote" : "local")} companion: diagram, comparison contents and columns, toolbar, wheel and drag navigation, title, remount, rollback, second terminal and disable/re-enable");
            }
            finally
            {
                if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            }
        }
    }

    private static void Scenario(E2eWorkspace app, PluginRuntime runtime, bool remote)
    {
        var window = app.Window;
        Until(() => app.Workbench.PluginLoader.Registry.Active.Contains("visualize"));
        var before = TerminalsE2E.TerminalTabs(app).Select(tab => tab.Id).ToHashSet();
        window.Layout.NewTerminal(window.Layout.View.FocusedCenter);
        Until(() => TerminalsE2E.TerminalTabs(app).Any(tab => !before.Contains(tab.Id)));
        var tab = TerminalsE2E.TerminalTabs(app).Single(tab => !before.Contains(tab.Id));
        var terminals = before.Count + 1;
        var terminal = TerminalsE2E.View(app, tab);
        var owner = new TerminalRef(terminal.Launch.WorkspaceRoot, terminal.Launch.TabKey);
        using var http = new HttpClient();
        string? mcpUrl = null;
        if (remote)
        {
            var backend = app.Terminals.Views.Last(view => view.SessionId == terminal.Launch.SessionId);
            Until(() => backend.Started.IsCompletedSuccessfully);
            terminal.Write("printf 'VISUALIZE_MCP_%s_\\n' \"$THINKRAIL_MCP_URL\"\r");
            const string pattern = @"VISUALIZE_MCP_(http://127\.0\.0\.1:\d+/mcp/[0-9a-f]+)_";
            Until(() => Regex.IsMatch(backend.Text, pattern));
            mcpUrl = Regex.Match(backend.Text, pattern).Groups[1].Value;
            var listed = Task.Run(() => Mcp("tools/list"));
            Until(() => listed.IsCompleted);
            Require(listed.Result["result"]!["tools"]!.AsArray().Any(tool => tool!["name"]!.GetValue<string>() == "visualize"), "The terminal's HTTP MCP table lists visualize.");
            var unknown = Task.Run(() => http.PostAsync(mcpUrl[..(mcpUrl.LastIndexOf('/') + 1)] + "unknown-token", new StringContent("{}", Encoding.UTF8, "application/json")));
            Until(() => unknown.IsCompleted);
            using var unknownReply = unknown.Result;
            Require(unknownReply.StatusCode == HttpStatusCode.NotFound, "An unknown terminal MCP token is refused.");
        }

        async Task<JsonNode> Mcp(string method, object? parameters = null)
        {
            using var reply = await http.PostAsync(mcpUrl, new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json"));
            reply.EnsureSuccessStatusCode();
            return JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
        }

        string Call(string arguments, bool error = false)
        {
            var answer = Task.Run(async () =>
            {
                if (mcpUrl is not null)
                {
                    var result = (await Mcp("tools/call", new { name = "visualize", arguments = JsonNode.Parse(arguments) }))["result"]!;
                    return (Text: result["content"]![0]!["text"]!.GetValue<string>(), Error: result["isError"]?.GetValue<bool>() ?? false);
                }
                var tool = runtime.McpTools(owner, owner.WorkspaceId).Single(tool => tool.Name == "visualize");
                var called = await tool.Call(JsonNode.Parse(arguments)!.AsObject(), CancellationToken.None);
                return (called.Text, called.Error);
            });
            Until(() => answer.IsCompleted);
            Require(answer.Result.Error == error, $"The visualize tool answered {(answer.Result.Error ? "an error" : "success")}: {answer.Result.Text}");
            return answer.Result.Text;
        }
        Control? Pane() => terminal.Companion;
        Button Chip() => app.Find<Button>("TerminalCompanion_visualize_visualization");
        string Texts(Control control) => string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        Control Named(Control scope, string name) => scope.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);
        bool Has(Control scope, string name) => scope.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name);

        // The tool draws a live view keyed to this very terminal: an embedded pane in the terminal's own body, not a tab.
        var drawn = Call("""{"type":"diagram","title":"Wired graph","mermaid":"graph TD;A-->B;"}""");
        Require(drawn.Contains("Rendered \"Wired graph\" in SharpRail (revision 1)", StringComparison.Ordinal), "The tool reports the drawing: " + drawn);
        Until(() => Pane() is { IsEffectivelyVisible: true } pane && Has(pane, "VisualizationPane"));
        var pane = Pane()!;
        Require(TerminalsE2E.TerminalTabs(app).Length == terminals, "The pane must not cost the terminal its tab.");
        Require(Texts(Chip()) == "Wired graph", "The companion reports the drawing's own title: " + Texts(Chip()));

        // The diagram is navigable in place.
        Until(() => Has(pane, "MermaidPanZoom"));
        TextBlock Level() => (TextBlock)Named(pane, "MermaidZoomLevel");
        Require(Level().Text == "100%", "The pane's diagram opens at 100%.");
        app.Click((Button)Named(pane, "MermaidZoomIn"));
        Until(() => Level().Text == "115%");
        var mounted = Named(pane, "MermaidPanZoom");
        var image = (Image)Named(pane, "DiagramImage");
        var beforeTheme = image.Source;
        var theme = Ui.Theme;
        Ui.Apply(theme with { Id = theme.Id + "-visualize-check" });
        Until(() => !ReferenceEquals(image.Source, beforeTheme));
        Require(ReferenceEquals(Named(pane, "MermaidPanZoom"), mounted) && Level().Text == "115%", "A completed theme rerender preserves diagram navigation and zoom.");
        beforeTheme = image.Source;
        Ui.Apply(theme);
        Until(() => !ReferenceEquals(image.Source, beforeTheme));
        app.Click((Button)Named(pane, "MermaidZoomReset"));
        Until(() => Level().Text == "100%");

        var viewer = (ScrollViewer)Named(pane, "MermaidPanZoom");
        var wheel = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, 80), window)!.Value;
        window.MouseWheel(wheel, new Vector(0, -1));
        Require(Level().Text == "100%", "An unmodified wheel scrolls without zooming.");
        var offset = viewer.Offset;
        window.MouseWheel(wheel, new Vector(0, 1), RawInputModifiers.Control);
        Require(Level().Text == "113%" && viewer.Offset == offset, "Control-wheel zooms by the shared bounded gesture and consumes scrolling.");
        window.MouseWheel(wheel, new Vector(0, -1), RawInputModifiers.Meta);
        Require(Level().Text == "100%", "Command-wheel reverses the same gesture.");
        for (var index = 0; index < 20; index++) app.Click((Button)Named(pane, "MermaidZoomIn"), freshGesture: false);
        Require(Level().Text == "600%", "Diagram zoom is capped at 600%.");
        for (var index = 0; index < 45; index++) app.Click((Button)Named(pane, "MermaidZoomOut"), freshGesture: false);
        Require(Level().Text == "25%", "Diagram zoom is bounded at 25%.");
        app.Click((Button)Named(pane, "MermaidZoomReset"));
        Until(() => Level().Text == "100%");

        // A zoomed diagram pans by dragging with the primary button.
        for (var index = 0; index < 8; index++) app.Click((Button)Named(pane, "MermaidZoomIn"), freshGesture: false);
        Until(() => viewer.Extent.Width > viewer.Viewport.Width && viewer.Extent.Height > viewer.Viewport.Height);
        viewer.Offset = default;
        var grab = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height / 2), window)!.Value;
        var release = grab - new Point(40, 30);
        window.MouseMove(grab); window.MouseDown(grab, MouseButton.Left); window.MouseMove(release); window.MouseUp(release, MouseButton.Left);
        Require(Math.Abs(viewer.Offset.X - 40) < 1 && Math.Abs(viewer.Offset.Y - 30) < 1, $"Dragging pans the zoomed diagram by the pointer's travel: {viewer.Offset}");
        app.Click((Button)Named(pane, "MermaidZoomReset"));

        // Calling again updates the same view in place, with every option's description, pros and cons.
        Call("""{"type":"comparison","title":"Wired graph","options":[{"name":"OptA","recommended":true,"description":"First path","pros":["Fast"],"cons":["Costly"]},{"name":"OptB","pros":["Cheap"]}]}""");
        Until(() => Texts(pane).Contains("OptA", StringComparison.Ordinal) && Has(pane, "ComparisonRecommended"));
        foreach (var text in new[] { "OptB", "First path", "Fast", "Costly", "Cheap" })
            Require(Texts(pane).Contains(text, StringComparison.Ordinal), $"The comparison shows {text}: {Texts(pane)}");
        Require(pane.GetLogicalDescendants().OfType<Control>().Count(control => control.Name == "ComparisonRecommended" && control.IsVisible) == 1, "Only the recommended option carries the badge.");
        Require(ReferenceEquals(Pane(), pane) && TerminalsE2E.TerminalTabs(app).Length == terminals, "An update redraws the open pane in place.");

        // Options sit side by side once the pane is at least 640 wide and stack below that; the splitter sets the width.
        var options = (Avalonia.Controls.Primitives.UniformGrid)Named(pane, "Options");
        var companion = terminal.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "TerminalCompanion");
        var width = companion.Width;
        foreach (var size in new[] { 760.0, 400.0 })
        {
            companion.Width = size;
            Settle();
            Require(options.Columns == (options.Bounds.Width >= 640 ? 2 : 1) && (size > 700) == (options.Columns == 2),
                $"Comparison columns follow the pane width: pane {size}, options {options.Bounds.Width}, columns {options.Columns}");
        }
        companion.Width = width;

        // Closing folds it back into the chip on the terminal; the chip reopens it.
        app.Click(Chip());
        Until(() => Pane() is null);
        Require(TerminalsE2E.TerminalTabs(app).Length == terminals, "Closing the pane keeps the terminal.");
        app.Click(Chip());
        Until(() => Pane() is { IsEffectivelyVisible: true });

        // A diagram the renderer refuses comes back to the agent as a tool error, and the pane keeps the last drawing that worked.
        var refused = Call("""{"type":"diagram","title":"Broken","mermaid":"flowchart TD; Start --> --> broken"}""", error: true);
        Require(refused.Contains("The diagram did not render", StringComparison.Ordinal) && refused.Contains("call visualize again", StringComparison.Ordinal),
            "A refused diagram reaches the agent: " + refused);
        Until(() => Texts(Chip()) == "Wired graph" && Texts(Pane()!).Contains("OptA", StringComparison.Ordinal));

        app.Click(Chip());
        Until(() => Pane() is null);
        using var peer = app.NewWindow();
        peer.Window.Activate();
        Until(() => ReferenceEquals(app.Workbench.ActiveWindow, peer.Window));
        Call("""{"type":"comparison","title":"Background drawing","options":[{"name":"Owner only"}]}""");
        Until(() => Pane() is not null && Texts(Pane()!).Contains("Owner only", StringComparison.Ordinal));
        Require(ReferenceEquals(app.Workbench.ActiveWindow, peer.Window), "A background drawing must not activate the terminal's window.");
        Require(!peer.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "VisualizationPane"), "The unrelated window must not receive the terminal's drawing.");
        peer.Window.Close();

        // Disabling the plugin unmounts its panes and chips; enabling it again resubscribes from scratch.
        void SetEnabled(bool enabled)
        {
            window.ShowSettings("Plugins");
            Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
            var row = settings.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "PluginRow_visualize");
            row.GetLogicalDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle").IsChecked = enabled;
            Until(() => app.Workbench.PluginLoader.Registry.Active.Contains("visualize") == enabled);
            settings.Close();
        }
        SetEnabled(false);
        Until(() => Pane() is null && !window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "TerminalCompanion_visualize_visualization"));
        SetEnabled(true);
        Call("""{"type":"comparison","title":"Remounted","options":[{"name":"After enable"}]}""");
        Until(() => Pane() is not null && Texts(Pane()!).Contains("After enable", StringComparison.Ordinal));
        Require(Texts(Chip()) == "Remounted", "The re-enabled plugin's fresh subscription titles the companion: " + Texts(Chip()));
        // A second terminal of the same workspace keeps its own lifetime: its drawing opens beside it alone.
        var others = TerminalsE2E.TerminalTabs(app).Select(next => next.Id).ToHashSet();
        window.Layout.NewTerminal(window.Layout.View.FocusedCenter);
        Until(() => TerminalsE2E.TerminalTabs(app).Any(next => !others.Contains(next.Id)));
        var second = TerminalsE2E.View(app, TerminalsE2E.TerminalTabs(app).Single(next => !others.Contains(next.Id)));
        var secondOwner = new TerminalRef(second.Launch.WorkspaceRoot, second.Launch.TabKey);
        if (remote)
        {
            var backend = app.Terminals.Views.Last(view => view.SessionId == second.Launch.SessionId);
            Until(() => backend.Started.IsCompletedSuccessfully);
        }
        var secondCall = Task.Run(async () =>
        {
            var tool = runtime.McpTools(secondOwner, secondOwner.WorkspaceId).Single(tool => tool.Name == "visualize");
            return await tool.Call(JsonNode.Parse("""{"type":"comparison","title":"Second","options":[{"name":"Second only"}]}""")!.AsObject(), CancellationToken.None);
        });
        Until(() => secondCall.IsCompleted && second.Companion is not null && Texts(second.Companion).Contains("Second only", StringComparison.Ordinal));
        Require(!secondCall.Result.Error && !Texts(second.Companion!).Contains("After enable", StringComparison.Ordinal), "Each terminal keeps its own drawing.");

    }
}