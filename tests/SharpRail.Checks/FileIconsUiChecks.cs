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
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static partial class FileIconsChecks
{
    internal static void UiChecks(string root)
    {
        SharedSvgLifetime();
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
                Until(() => app.Window.WorkspaceMounted);
                app.ShowTool("files");
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
        Until(() => app.Window.WorkspaceMounted);
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
        .Single(control => control.Name == "FileTypeIcon" && Equals(control.Tag, $"file-type:{name}"));

    private static void Loaded(Control scope, string name) => Until(() => scope.GetLogicalDescendants().OfType<Control>()
        .Any(control => control.Name == "FileTypeIcon" && Equals(control.Tag, $"file-type:{name}") &&
            control.GetLogicalDescendants().OfType<SvgAsset>().Any(svg => svg.Source is not null && svg.IsEffectivelyVisible)));

    private static byte[] Raster(E2eWorkspace app, Control icon) => Raster(app.Window, icon);

    private static byte[] Raster(Window window, Control icon)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame()!;
        var at = icon.TranslatePoint(default, window)!.Value * window.RenderScaling;
        var width = (int)Math.Ceiling(icon.Bounds.Width * window.RenderScaling);
        var height = (int)Math.Ceiling(icon.Bounds.Height * window.RenderScaling);
        var pixels = new byte[width * height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect((int)at.X, (int)at.Y, width, height), pinned.AddrOfPinnedObject(), pixels.Length, width * 4); }
        finally { pinned.Free(); }
        return pixels;
    }

    private static void SharedSvgLifetime()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><path fill='currentColor' d='M0 0H16V16H0Z'/></svg>");
        var first = new SvgAsset(bytes, 16, Brushes.White);
        var second = new SvgAsset(bytes, 16, Brushes.White);
        var tinted = new SvgAsset(bytes, 16, Brushes.Red);
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { first, second, tinted } };
        var window = new Window { Width = 80, Height = 40, Background = Brushes.Black, Content = panel };
        window.Show();
        try
        {
            Settle(50);
            var shared = first.Source;
            Require(shared is not null && ReferenceEquals(shared, second.Source) && !ReferenceEquals(shared, tinted.Source),
                "Mounted icons share parsed SVGs only when both asset bytes and tint match.");
            var before = Raster(window, second);
            Require(before.Where((_, index) => index % 4 < 3).All(channel => channel == 255), "The shared white SVG renders its filled square.");
            Require(!Raster(window, tinted).SequenceEqual(before), "A different tint renders independently.");
            panel.Children.Remove(first); Settle(50);
            Require(first.Source is null && ReferenceEquals(second.Source, shared) && Raster(window, second).SequenceEqual(before),
                "Recycling one icon releases its reference without disposing a still-visible sibling's SVG.");
            panel.Children.Remove(second);
            Require(second.Source is null, "The last detached icon releases its image.");
            panel.Children.Add(first); Settle(50);
            Require(first.Source is not null && !ReferenceEquals(first.Source, shared) && Raster(window, first).SequenceEqual(before),
                "A later attachment parses a fresh source after the last user released the old one.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS SVG icon sharing: matching assets/tints, rendered sibling lifetime and release after last detach");
    }

    private static void Scenario(E2eWorkspace app, bool git)
    {
        TwoTone(app);
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
            app.ShowTool("changes");
            Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file-type:python")));
            Loaded(app.Window, "python");
            app.Click(app.Find<ToggleButton>("ChangesTree"));
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
            Loaded(app.Find<TreeView>("ChangesTree"), "python");
        }
        else Console.WriteLine("SKIP file-icons change rows: set SHARPRAIL_TEST_GIT_SOURCE to an existing fork clone.");

        app.ShowTool("files");
        app.Window.ShowSettings("Appearance");
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        Switch Toggle() => settings.GetLogicalDescendants().OfType<Switch>().Single(control => control.Name == "FileTypeIconsEnabled");
        Until(() => Toggle().IsEnabled);
        Require(Toggle().IsChecked, "Appearance shows core file-type icons on by default.");
        app.Click(Toggle(), freshGesture: false);
        Until(() => !app.Workbench.State.Current.Settings.FileTypeIconsEnabled && !Toggle().IsChecked);
        Until(() => !app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "FileTypeIcon"));
        Require(app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible),
            "Disabling File Icons restores core's plain file glyph.");
        app.Click(Toggle(), freshGesture: false);
        Until(() => app.Workbench.State.Current.Settings.FileTypeIconsEnabled && Toggle().IsChecked);
        settings.Close();
        Loaded(app.FileRow("README.md"), "readme");
        Loaded(app.Tab("README.md"), "readme");
    }

    private static void TwoTone(E2eWorkspace app)
    {
        var icon = WorkbenchWindow.CoreFileIcon("python", Brushes.White, 96);
        var window = new Window { Width = 96, Height = 96, Background = Brushes.Black, Content = icon };
        window.Show();
        try
        {
            Loaded(window, "python");
            Settle(100);
            using var frame = window.CaptureRenderedFrame()!;
            var pixels = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect(frame.PixelSize), pinned.AddrOfPinnedObject(), pixels.Length, frame.PixelSize.Width * 4); }
            finally { pinned.Free(); }
            int Channel(int x, int y) => pixels[((int)(y * window.RenderScaling) * frame.PixelSize.Width + (int)(x * window.RenderScaling)) * 4];
            Require(Channel(40, 24) >= 250 && Channel(48, 64) is >= 110 and <= 120,
                "The Python icon renders its primary at full colour and its secondary at 45% opacity, as in the fork.");
        }
        finally { window.Close(); }
    }

    private static void UnavailableAssets(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "file-icons-fallback"));
        Until(() => app.Window.WorkspaceMounted);
        foreach (var malformed in new[] { false, true })
        {
            var icon = malformed ? new AssetIcon("malformed", "file", null, 14) : WorkbenchWindow.CoreFileIcon("missing-icon");
            if (malformed) ((AssetIcon)icon).Show(System.Text.Encoding.UTF8.GetBytes("not an SVG"), Brushes.White, 14);
            var window = new Window { Width = 80, Height = 80, Content = icon };
            window.Show();
            try
            {
                Settle(100);
                Require(icon.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible),
                    "Missing and malformed SVG assets draw the plain file glyph rather than an empty slot.");
            }
            finally { window.Close(); }
        }
        Console.WriteLine("PASS core file-icons asset fallback: missing and malformed SVGs retain the file glyph");
    }
}