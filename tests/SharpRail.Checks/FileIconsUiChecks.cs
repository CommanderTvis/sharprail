using System.Net;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Remote;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Docking;
using SharpRail.UI.Plugins;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static partial class FileIconsChecks
{
    internal static void UiChecks(string root)
    {
        DeferredCatalogClick(root);
        foreach (var remote in new[] { false, true })
        {
            var mode = remote ? "remote" : "local";
            var workspace = Path.Combine(root, "file-icons-ui", mode);
            Directory.CreateDirectory(workspace);
            var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
            if (source is not null)
            {
                IsolatedGit.Run(workspace, "init", "-b", "main");
                IsolatedGit.Run(workspace, "fetch", "--no-tags", "--depth=1", source, "refs/remotes/origin/claude-code-integration-plugin-api");
                IsolatedGit.Run(workspace, "checkout", "-B", "main", "FETCH_HEAD");
            }
            File.WriteAllText(Path.Combine(workspace, "README.md"), "# Icon fixture\n");
            File.WriteAllText(Path.Combine(workspace, "SPEC.md"), "# Icon specification\n");
            File.WriteAllBytes(Path.Combine(workspace, "logo.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
            File.WriteAllText(Path.Combine(workspace, "sample.pdf"), "%PDF-1.7\n");
            File.WriteAllText(Path.Combine(workspace, "unknown.qqqq"), "unknown\n");
            File.WriteAllText(Path.Combine(workspace, "server.py"), "print('hi')\n");
            var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "icons", workspace + "-state") : null;
            try
            {
                if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
                using var app = server is null ? new E2eWorkspace(workspace)
                    : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "icons", workspace, workspace + "-profile", workspace);
                Until(() => app.Window.WorkspaceMounted && app.Workbench.PluginLoader.Registry.Active.Contains("file-icons"));
                app.Click(app.Find<Button>("Tab_files"));
                Require(app.Window.Layout.State.Groups.Where(group => group.Tools.Any(tab => tab.Id == "files")).All(group => app.Window.Layout.Selected(group.Id)?.Id == "files"),
                    "Clicking Files selects its rail tab before checking icons.");
                Scenario(app, source is not null);
                Console.WriteLine($"PASS file-icons {mode} UI: typed tree/tab/change icons, theme raster, resize retention and disable/re-enable");
            }
            finally
            {
                if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            }
        }
        UnavailableAssets(root);
    }

    private static void DeferredCatalogClick(string root)
    {
        TaskCompletionSource? gitReady = null;
        var directory = IsolatedGit.Repository(Path.Combine(root, "file-icons-deferred-click"));
        using var app = new E2eWorkspace(directory, openFiles: false, prepare: host => gitReady = host.HoldGit());
        Until(() => app.Workbench.PluginLoader.Registry.Active.Contains("file-icons"));
        var button = app.Find<Button>("Tab_files");
        app.Window.UpdateLayout();
        button.BringIntoView();
        Settle(550);
        var point = button.TranslatePoint(new Point(Math.Min(24, button.Bounds.Width / 2), button.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        gitReady!.SetResult();
        Until(() => app.Window.Layout.Tools.Any(tool => tool.Id == "plugin:branch-graph:graph"));
        app.Window.MouseUp(point, MouseButton.Left);
        Until(() => app.Window.Layout.State.Groups.Where(group => group.Tools.Any(tab => tab.Id == "files")).All(group => app.Window.Layout.Selected(group.Id)?.Id == "files"));
        Until(() => ReferenceEquals(button, app.Find<Button>("Tab_files")) || app.Find<Button>("Tab_files").IsFocused);
        Console.WriteLine("PASS file-icons deferred catalog: Git discovery during a tab press preserves its click");
    }

    private static Control Asset(Control scope, string name) => scope.GetLogicalDescendants().OfType<Control>()
        .Single(control => control.Name == "PluginAssetIcon" && Equals(control.Tag, $"asset:file-icons/{name}.svg"));

    private static void Loaded(Control scope, string name) => Until(() => scope.GetLogicalDescendants().OfType<Control>()
        .Any(control => control.Name == "PluginAssetIcon" && Equals(control.Tag, $"asset:file-icons/{name}.svg") &&
            control.GetLogicalDescendants().OfType<SvgAsset>().Any(svg => svg.Source is not null && svg.IsEffectivelyVisible)));

    private static byte[] Raster(E2eWorkspace app, Control icon)
    {
        app.Window.UpdateLayout();
        using var frame = app.Window.CaptureRenderedFrame()!;
        var at = icon.TranslatePoint(default, app.Window)!.Value * app.Window.RenderScaling;
        var width = (int)Math.Ceiling(icon.Bounds.Width * app.Window.RenderScaling);
        var height = (int)Math.Ceiling(icon.Bounds.Height * app.Window.RenderScaling);
        var pixels = new byte[width * height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect((int)at.X, (int)at.Y, width, height), pinned.AddrOfPinnedObject(), pixels.Length, width * 4); }
        finally { pinned.Free(); }
        return pixels;
    }

    private static void Scenario(E2eWorkspace app, bool git)
    {
        foreach (var (file, typeIcon) in new[] { ("README.md", "readme"), ("SPEC.md", "markdown"), ("logo.png", "image"), ("sample.pdf", "pdf"), ("unknown.qqqq", "file") })
            Loaded(app.FileRow(file), typeIcon);
        var row = app.FileRow("README.md");
        row.BringIntoView();
        Settle(100);
        var icon = Asset(row, "readme");
        var image = icon.GetLogicalDescendants().OfType<SvgAsset>().Single();
        var before = Raster(app, icon);
        var original = Ui.Theme;
        var colors = original.Colors.ToDictionary();
        colors["text"] = Color.Parse("#ff3366");
        var source = image.Source;
        Ui.Apply(original with { Id = original.Id + "-file-icons", Colors = colors });
        Until(() => !ReferenceEquals(image.Source, source));
        Settle(50);
        Require(!Raster(app, icon).SequenceEqual(before), "The SVG follows the theme's text colour in the rendered frame.");
        Require(ReferenceEquals(Asset(app.FileRow("README.md"), "readme"), icon), "A theme change retains the row's icon control.");
        Ui.Apply(original);

        app.Open("README.md", keep: true);
        Loaded(app.Tab("README.md"), "readme");
        var tabIcon = Asset(app.Tab("README.md"), "readme");
        var splitter = app.Find<ResizeHandle>("leftSeparator");
        var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        for (var index = 1; index <= 6; index++)
        {
            app.Window.MouseMove(point + new Point(index * 5, 0));
            app.Window.UpdateLayout();
            Require(ReferenceEquals(Asset(app.Tab("README.md"), "readme"), tabIcon), "Resizing a column retains its tab icon instead of remounting it each frame.");
        }
        app.Window.MouseUp(point + new Point(30, 0), MouseButton.Left);

        if (git)
        {
            var refresh = app.Window.RefreshAsync();
            Until(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            app.Click(app.Find<Button>("Tab_changes"));
            Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "asset:file-icons/python.svg")));
            Loaded(app.Window, "python");
            app.Click(app.Find<ToggleButton>("ChangesTree"));
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
            Loaded(app.Find<TreeView>("ChangesTree"), "python");
        }
        else Console.WriteLine("SKIP file-icons change rows: set SHARPRAIL_TEST_GIT_SOURCE to an existing fork clone.");

        app.Click(app.Find<Button>("Tab_files"));
        var disabling = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled("file-icons", false));
        Until(() => disabling.IsCompleted && !app.Workbench.PluginLoader.Registry.Active.Contains("file-icons"));
        disabling.GetAwaiter().GetResult();
        Until(() => !app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "PluginAssetIcon"));
        Require(app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible),
            "Disabling File Icons restores core's plain file glyph.");
        var enabling = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled("file-icons", true));
        Until(() => enabling.IsCompleted && app.Workbench.PluginLoader.Registry.Active.Contains("file-icons"));
        enabling.GetAwaiter().GetResult();
        Loaded(app.FileRow("README.md"), "readme");
        Loaded(app.Tab("README.md"), "readme");
    }

    private static void UnavailableAssets(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "file-icons-fallback"));
        Until(() => app.Workbench.PluginLoader.Registry.Active.Contains("file-icons"));
        var read = PluginIcons.ReadAsset;
        try
        {
            foreach (var malformed in new[] { false, true })
            {
                PluginIcons.ReadAsset = (_, _, _) => Task.FromResult<byte[]?>(malformed ? System.Text.Encoding.UTF8.GetBytes("not an SVG") : null);
                var icon = PluginIcons.Resolve("asset:file-icons/fallback-" + Guid.NewGuid() + ".svg", app.Workbench.PluginLoader.Registry.Entry("file-icons"), size: 14, fallbackGlyph: "file");
                var window = new Window { Width = 80, Height = 80, Content = icon };
                window.Show();
                Settle(100);
                Require(icon.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible),
                    "Missing and malformed SVG assets draw the plain file glyph rather than an empty slot.");
                window.Close();
            }
        }
        finally { PluginIcons.ReadAsset = read; }
        Console.WriteLine("PASS file-icons asset fallback: missing and malformed SVGs retain the file glyph");
    }
}