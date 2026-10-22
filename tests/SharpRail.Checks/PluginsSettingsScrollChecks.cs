using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Plugins.Api;
using SharpRail.UI.Panels;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.Checks;

// Settings › Plugins with a long roster (every language server is its own plugin): scrolling realizes only the cards
// near the viewport, a host broadcast mid-scroll neither recreates cards nor moves the scroll, and toggles still work.
// Prints the realized and created card counts, controls re-measured per step and the scroll frame time.
internal static class PluginsSettingsScrollChecks
{
    private const int Plugins = 48;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var profile = new ProfileStore(Path.Combine(root, "plugins-scroll-profile"));
        var store = profile.OpenState();
        var roster = Enumerable.Range(0, Plugins).Select(index => new PluginRosterEntry($"lsp-{index:00}", $"Language server {index:00}", "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled)
        {
            Description = $"Language server {index:00} for its files: completion, navigation, diagnostics and formatting through the shared code intelligence session.",
            Contributes = new() { SideTools = [new("panel", "Panel", "puzzle", PluginToolSide.Right)] }
        }).ToArray();
        var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, roster);
        var state = new SharedState(host, profile.Data.Preferences);
        state.Start();
        E2eWorkspace.Until(() => state.Current.Plugins.Count == Plugins);

        var page = new PluginsSettings(state, null, new Window());
        var scroller = new ScrollViewer { Content = page, Padding = new Thickness(16) };
        var window = new Window { Width = 800, Height = 600, Content = scroller };
        window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();

        Border[] Realized() => [.. window.GetVisualDescendants().OfType<Border>().Where(row => row.Name == "PluginRow" && row.IsVisible)];
        var created = new HashSet<Border>(ReferenceEqualityComparer.Instance);
        var mounted = Realized().Length;
        foreach (var row in Realized()) created.Add(row);

        var invalid = 0;
        var frames = new List<double>();
        var steps = 0;
        void Step(double delta)
        {
            scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y + delta, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
            var clock = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            invalid += window.GetVisualDescendants().OfType<Layoutable>().Count(control => !control.IsMeasureValid);
            window.UpdateLayout();
            window.CaptureRenderedFrame();
            frames.Add(clock.Elapsed.TotalMilliseconds);
            steps++;
            foreach (var row in Realized()) created.Add(row);
        }
        window.CaptureRenderedFrame();
        for (var pass = 0; pass < 3; pass++)
        {
            for (var guard = 0; guard < 400 && scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 1; guard++) Step(60);
            for (var guard = 0; guard < 400 && scroller.Offset.Y > 0; guard++) Step(-60);
        }
        frames.Sort();
        var refresh = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++) page.Refresh();
        var refreshMs = refresh.Elapsed.TotalMilliseconds / 100;
        var realizedPeak = Realized().Length;
        Console.WriteLine($"plugins-settings scroll: {Plugins} plugins, {mounted} cards realized on mount, {created.Count} card controls created over {steps} scroll steps, " +
            $"{invalid} controls re-measured, frame median {frames[frames.Count / 2]:F2} ms, p95 {frames[(int)(frames.Count * .95)]:F2} ms, extent {scroller.Extent.Height:F0}, unchanged-roster refresh {refreshMs:F3} ms");
        Require(scroller.Extent.Height > scroller.Viewport.Height * 3, "Every plugin must still contribute to the scroll extent.");
        Require(mounted < Plugins / 2 && realizedPeak < Plugins / 2, $"Only the cards near the viewport may be realized ({mounted} on mount, {realizedPeak} after scrolling).");
        Require(created.Count <= mounted * 3, $"Scrolling must recycle cards rather than create one per plugin ({created.Count} created, {mounted} realized).");

        // A broadcast mid-scroll (an unrelated setting, then another plugin's roster change) keeps the cards and the offset.
        Step(400);
        var offset = scroller.Offset;
        var visible = Realized().ToDictionary(row => (string)row.Tag!, row => row, StringComparer.Ordinal);
        var before = created.Count;
        var theme = state.ChangeAsync(HostStateChange.Setting("theme", "light"));
        E2eWorkspace.Until(() => theme.IsCompleted);
        var enable = state.ChangeAsync(HostStateChange.PluginEnabled("lsp-47", true));
        E2eWorkspace.Until(() => enable.IsCompleted);
        E2eWorkspace.Until(() => state.Current.Plugins.Single(entry => entry.Id == "lsp-47").Status == PluginStatus.Active);
        E2eWorkspace.Settle(50);
        window.UpdateLayout();
        Require(scroller.Offset == offset, $"A broadcast must not move the scroll ({offset} became {scroller.Offset}).");
        Require(Realized().All(row => visible.TryGetValue((string)row.Tag!, out var kept) && ReferenceEquals(kept, row)) && visible.Count == Realized().Length,
            "A broadcast must update the visible cards in place, not recreate them.");
        foreach (var row in Realized()) created.Add(row);
        Require(created.Count == before, "A broadcast must not create cards.");

        // Toggling a visible card turns its plugin on through the host and the switch follows the snapshot.
        var target = Realized().OrderBy(row => row.Bounds.Y).First(row => row.TranslatePoint(default, scroller) is { Y: >= 0 });
        var id = (string)target.Tag!;
        var toggle = target.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle");
        Require(toggle.IsChecked == false, "A disabled plugin's switch reads off.");
        toggle.IsChecked = true;
        E2eWorkspace.Until(() => state.Current.Plugins.Single(entry => entry.Id == id).Status == PluginStatus.Active);
        E2eWorkspace.Settle(50);
        window.UpdateLayout();
        var after = Realized().Single(row => (string)row.Tag! == id);
        Require(after.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle").IsChecked == true, "The switch follows the host's snapshot.");
        Require(scroller.Offset == offset, "Toggling must not move the scroll.");
        window.Close();
        state.Dispose();
        Console.WriteLine("PASS plugins settings: long roster virtualized and recycled, broadcasts update cards in place, toggles work");
        BuiltinIcons();
    }

    // Every builtin plugin's card draws its own mark: a language server shows the icon Code Intelligence shows for it.
    private static void BuiltinIcons()
    {
        var read = PluginIcons.ReadAsset;
        PluginIcons.ReadAsset = async (id, path, token) =>
        {
            var file = Path.Combine(AppContext.BaseDirectory, "plugins", id, path);
            return File.Exists(file) ? await File.ReadAllBytesAsync(file, token) : null;
        };
        try
        {
            var manifests = SharpRail.Host.Core.Plugins.BuiltinPlugins.All.Select(item => item.Manifest).ToArray();
            var panel = new StackPanel();
            var icons = manifests.Select(manifest =>
            {
                var entry = new PluginRosterEntry(manifest.Id, manifest.Label, manifest.Icon, manifest.Version, manifest.WireVersion, PluginOrigin.Builtin, PluginStatus.Active) { Assets = manifest.Assets };
                var icon = PluginIcons.Resolve(entry.Icon, entry, null, 16);
                panel.Children.Add(icon);
                return (manifest, icon);
            }).ToArray();
            var window = new Window { Width = 200, Height = 200, Content = panel };
            window.Show();
            static bool Drawn(Control icon) => icon.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null);
            foreach (var (manifest, icon) in icons)
            {
                if (manifest.Icon.StartsWith("asset:", StringComparison.Ordinal))
                    E2eWorkspace.Until(() => { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); return Drawn(icon); });
                else Require(PluginIcons.Glyph(manifest.Icon) != PluginIcons.Fallback || manifest.Icon.Contains("puzzle", StringComparison.Ordinal),
                    $"Builtin plugin {manifest.Id} names icon {manifest.Icon}, which draws the generic puzzle glyph.");
            }
            foreach (var (manifest, host) in SharpRail.Host.Core.Plugins.BuiltinPlugins.LanguageServerEntries)
            {
                var server = (SharpRail.Plugins.Api.Host.LanguageServerPlugin)host!;
                var file = Path.Combine(AppContext.BaseDirectory, "plugins", manifest.Id, manifest.Assets ?? "", manifest.Icon["asset:".Length..]);
                Require(server.Spec.Icon is { } mark && File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(mark.Data),
                    $"{manifest.Id}'s plugin icon must be the mark Code Intelligence shows for {server.Spec.Name}.");
            }
            window.Close();
            Console.WriteLine($"PASS plugins settings: all {manifests.Length} builtin plugin cards draw an icon; language servers show their Code Intelligence marks");
        }
        finally { PluginIcons.ReadAsset = read; }
    }
}