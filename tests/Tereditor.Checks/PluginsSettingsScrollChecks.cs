using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Checks.E2E;
using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Plugins.Api;
using Tereditor.UI.Panels;
using Tereditor.UI.Plugins;
using Tereditor.UI.Rendering;
using Tereditor.UI.State;

namespace Tereditor.Checks;

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
        CategoryLinkAlignsTop(root);
        AssetIconDecodes(root);
        BuiltinIcons();
        TailscaleLock(root, through: true);
        TailscaleLock(root, through: false);
        DependenciesAndEscape(root);
        UnavailableDependencies(root);
    }

    // A category link brings its heading card to the top of the page, not to the bottom edge BringIntoView would leave it at.
    private static void CategoryLinkAlignsTop(string root)
    {
        var profile = new ProfileStore(Path.Combine(root, "plugins-link-profile"));
        var store = profile.OpenState();
        var roster = Enumerable.Range(0, 40).Select(index => new PluginRosterEntry($"cat-{index:00}", $"Plugin {index:00}", "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled)
        {
            Category = index < 20 ? "Alpha" : "Beta",
            Description = "A plugin with a description long enough to take some room on the card."
        }).ToArray();
        var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, roster);
        var state = new SharedState(host, profile.Data.Preferences);
        state.Start();
        E2eWorkspace.Until(() => state.Current.Plugins.Count == 40);
        var page = new PluginsSettings(state, null, new Window());
        var scroller = new ScrollViewer { Content = page };
        var window = new Window { Width = 800, Height = 600, Content = scroller };
        window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        page.ScrollToCategory("Beta");
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var card = window.GetVisualDescendants().OfType<Border>().First(row => row.Name == "PluginRow" && (string?)row.Tag == "cat-20");
        var top = card.TranslatePoint(default, scroller)!.Value.Y;
        var max = scroller.Extent.Height - scroller.Viewport.Height;
        Require(top is >= -1 and <= 12 || scroller.Offset.Y >= max - 1, $"A category link must bring its heading to the top of the page (top {top:F0}, offset {scroller.Offset.Y:F0} of {max:F0}).");
        window.Close();
        state.Dispose();
        Console.WriteLine($"PASS plugins settings: category link aligns its heading to the top (top {top:F0})");
    }

    // Language servers draw PNG marks (up to 256 px): scrolling realizes cards again and again, so a mark is decoded once
    // per plugin, never once per realization on the UI thread.
    private static void AssetIconDecodes(string root)
    {
        var profile = new ProfileStore(Path.Combine(root, "plugins-decode-profile"));
        var store = profile.OpenState();
        byte[] png;
        using (var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(256, 256), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul))
        using (var stream = new MemoryStream()) { bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); png = stream.ToArray(); }
        var roster = Enumerable.Range(0, Plugins).Select(index => new PluginRosterEntry($"png-{index:00}", $"Server {index:00}", "asset:Icon.png", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled)
        {
            Assets = "assets",
            Description = "A language server with its own PNG mark and a description long enough to give the card some height."
        }).ToArray();
        var read = PluginIcons.ReadAsset;
        PluginIcons.ReadAsset = (_, _, _) => Task.FromResult<byte[]?>([.. png]);
        try
        {
            var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, roster);
            var state = new SharedState(host, profile.Data.Preferences);
            state.Start();
            E2eWorkspace.Until(() => state.Current.Plugins.Count == Plugins);
            var scroller = new ScrollViewer { Content = new PluginsSettings(state, null, new Window()), Padding = new Thickness(16) };
            var window = new Window { Width = 800, Height = 600, Content = scroller };
            window.Show();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            for (var pass = 0; pass < 2; pass++)
            {
                for (var guard = 0; guard < 400 && scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 1; guard++)
                { scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + 60); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                for (var guard = 0; guard < 400 && scroller.Offset.Y > 0; guard++)
                { scroller.Offset = scroller.Offset.WithY(Math.Max(0, scroller.Offset.Y - 60)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            }
            Console.WriteLine($"plugins-settings icons: {AssetIcon.Decodes} PNG decodes for {Plugins} plugins over two full scroll passes");
            Require(AssetIcon.Decodes <= Plugins, $"A PNG mark must decode once per plugin, not once per realization ({AssetIcon.Decodes} decodes, {Plugins} plugins).");
            window.Close();
            state.Dispose();
        }
        finally { PluginIcons.ReadAsset = read; }
    }

    // On a client connected through Tailscale, the Tailscale card and those of plugins it depends on cannot be switched off.
    private static void TailscaleLock(string root, bool through)
    {
        var profile = new ProfileStore(Path.Combine(root, "plugins-tailscale-" + through));
        var store = profile.OpenState();
        PluginRosterEntry Entry(string id, params string[] dependsOn) => new(id, id, "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled) { DependsOn = dependsOn };
        var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, Entry("tailscale", "network"), Entry("network"), Entry("other")) { ThroughTailscale = through };
        var state = new SharedState(host, profile.Data.Preferences);
        state.Start();
        var enable = state.ChangeAsync([.. new[] { "tailscale", "network", "other" }.Select(id => HostStateChange.PluginEnabled(id, true))]);
        E2eWorkspace.Until(() => enable.IsCompleted && state.Current.Plugins.All(entry => entry.Status == PluginStatus.Active));
        var page = new PluginsSettings(state, null, new Window());
        var window = new Window { Width = 800, Height = 600, Content = new ScrollViewer { Content = page } };
        window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        ToggleSwitch Toggle(string id) => window.GetVisualDescendants().OfType<Border>().Single(row => row.Name == "PluginRow" && (string?)row.Tag == id)
            .GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle");
        foreach (var id in new[] { "tailscale", "network" })
            Require(Toggle(id).IsEnabled == !through && (ToolTip.GetTip(Toggle(id)) as string == (through ? TailscaleDependence.Refusal : null)),
                $"{id}'s switch must be {(through ? "locked with the reason on a Tailscale client" : "free on a client connected another way")}.");
        Require(Toggle("other").IsEnabled, "An unrelated plugin stays switchable.");
        if (!through)
        {
            Toggle("tailscale").IsChecked = false;
            E2eWorkspace.Until(() => state.Current.Plugins.Single(entry => entry.Id == "tailscale").Status == PluginStatus.Disabled);
        }
        window.Close();
        state.Dispose();
        Console.WriteLine($"PASS plugins settings: Tailscale and its dependencies {(through ? "locked on a Tailscale client" : "switchable on another transport")}");
    }

    // In Settings' overlay: switching on a plugin whose dependencies are off asks to enable the whole chain (declining
    // leaves all off, agreeing turns all on), and after any switch Escape closes the open dialog first, then Settings.
    private static void DependenciesAndEscape(string root)
    {
        var profile = new ProfileStore(Path.Combine(root, "plugins-escape-profile"));
        var store = profile.OpenState();
        PluginRosterEntry Entry(string id, params string[] dependsOn) => new(id, id, "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled) { DependsOn = dependsOn };
        var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, Entry("leaf", "twig"), Entry("twig", "trunk"), Entry("trunk"), Entry("plain"), Entry("mover"));
        var state = new SharedState(host, profile.Data.Preferences);
        state.Start();
        E2eWorkspace.Until(() => state.Current.Plugins.Count == 5);
        var window = new OverlayWindow { Width = 800, Height = 600 };
        window.Show();
        PluginsSettings page = null!;
        var settings = new Tereditor.UI.OverlayHost();
        window.Root.Children.Add(settings);
        var open = false;
        // A page stops following the host once detached, so each opening builds its own, as Settings does.
        void Open() { open = true; page = new PluginsSettings(state, null, window); settings.Show(new ScrollViewer { Content = page }, () => { open = false; settings.Hide(); }); Pump(); }
        void Pump() { for (var pass = 0; pass < 5; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); } }
        string Focused() => window.FocusManager?.GetFocusedElement() is Control control ? control.GetType().Name + "#" + control.Name : "nothing";
        void Escape() { window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null); Pump(); }
        ToggleSwitch Toggle(string id) => page.Card(id)!.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle");
        PluginStatus Status(string id) => state.Current.Plugins.Single(entry => entry.Id == id).Status;
        IReadOnlyList<Window> Dialogs() => Tereditor.Plugins.UI.Kit.DialogWindow.On(window);
        void Flip(string id) { var toggle = Toggle(id); toggle.Focus(); Pump(); toggle.IsChecked = !toggle.IsChecked; Pump(); }

        // A plain switch, then Escape.
        Open();
        Flip("plain");
        E2eWorkspace.Until(() => Status("plain") == PluginStatus.Active);
        Pump();
        Console.WriteLine("plugins-settings escape: after a plain switch focus is on " + Focused());
        Escape();
        Require(!open, "Escape closes Settings after a plugin is switched (focus on " + Focused() + ").");

        // A card whose plugin the host refuses moves under Unavailable: the switch that had focus leaves the tree, and
        // Escape, which then starts at the window, still closes Settings.
        Open();
        Toggle("mover").Focus();
        Pump();
        host.Replace(Entry("mover") with { Status = PluginStatus.Refused, Reason = "not available here" });
        E2eWorkspace.Until(() => { Pump(); return page.Listing().Contains("PluginCategory_Unavailable"); });
        Console.WriteLine("plugins-settings escape: after the card moved focus is on " + Focused());
        Escape();
        Require(!open, "Escape closes Settings after the focused card moved (focus on " + Focused() + ").");

        // A switch whose chain is off asks first; Escape declines and leaves every plugin off, a second Escape closes Settings.
        Open();
        Flip("leaf");
        E2eWorkspace.Until(() => { Pump(); return Dialogs().Count == 1; });
        var question = string.Join(" ", ((Control)((Tereditor.Plugins.UI.Kit.DialogWindow)Dialogs()[0]).Surface!).GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        Require(question.Contains("twig", StringComparison.Ordinal) && question.Contains("trunk", StringComparison.Ordinal), "The question names the whole chain: " + question);
        Escape();
        Require(Dialogs().Count == 0 && open, "The first Escape closes only the dialog (focus on " + Focused() + ").");
        E2eWorkspace.Settle(50);
        Require(new[] { "leaf", "twig", "trunk" }.All(id => Status(id) == PluginStatus.Disabled && !state.Current.PluginSettings.ContainsKey(id)), "Declining leaves the plugin and its chain off.");
        Console.WriteLine("plugins-settings escape: after declining focus is on " + Focused());
        Escape();
        Require(!open, "The second Escape closes Settings (focus on " + Focused() + ").");

        // Agreeing turns the chain on with the plugin; Escape still closes Settings.
        Open();
        Flip("leaf");
        E2eWorkspace.Until(() => { Pump(); return Dialogs().Count == 1; });
        ((Control)((Tereditor.Plugins.UI.Kit.DialogWindow)Dialogs()[0]).Surface!).GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PluginEnableDependencies")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        E2eWorkspace.Until(() => new[] { "leaf", "twig", "trunk" }.All(id => Status(id) == PluginStatus.Active));
        Pump();
        Console.WriteLine("plugins-settings escape: after agreeing focus is on " + Focused());
        Escape();
        Require(!open, "Escape closes Settings after the chain is enabled (focus on " + Focused() + ").");
        window.Close();
        state.Dispose();
        Console.WriteLine("PASS plugins settings: enabling asks for the whole dependency chain, and Escape closes the dialog, then Settings");
    }

    // Switching on a plugin with a missing or failed dependency anywhere in its chain is refused at once: no question, the
    // switch stays off, a notice names the dependency and why, and no setting is written.
    private static void UnavailableDependencies(string root)
    {
        var profile = new ProfileStore(Path.Combine(root, "plugins-unavailable-profile"));
        var store = profile.OpenState();
        PluginRosterEntry Entry(string id, params string[] dependsOn) => new(id, id, "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled) { DependsOn = dependsOn };
        var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, Entry("orphan", "bridge"), Entry("bridge", "ghost"),
            Entry("leaner", "broken"), Entry("broken") with { Status = PluginStatus.Failed, Reason = "activation threw" });
        var state = new SharedState(host, profile.Data.Preferences);
        state.Start();
        E2eWorkspace.Until(() => state.Current.Plugins.Count == 4);
        var window = new OverlayWindow { Width = 800, Height = 600 };
        window.Show();
        var page = new PluginsSettings(state, null, window);
        window.Root.Children.Add(new ScrollViewer { Content = page });
        void Pump() { for (var pass = 0; pass < 5; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); } }
        Pump();
        ToggleSwitch Toggle(string id) => page.Card(id)!.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle");
        TextBlock Notice() => page.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PluginsError");
        foreach (var (id, chain, expected) in new[]
        {
            ("orphan", new[] { "orphan", "bridge", "ghost" }, "Can't turn on orphan: it needs ghost, which is not installed."),
            ("leaner", new[] { "leaner", "broken" }, "Can't turn on leaner: it needs broken, which failed to load.")
        })
        {
            Toggle(id).IsChecked = true;
            Pump();
            E2eWorkspace.Settle(50);
            Pump();
            Require(Tereditor.Plugins.UI.Kit.DialogWindow.On(window).Count == 0, $"Switching on {id} asks nothing.");
            Require(Toggle(id).IsChecked == false && state.Current.Plugins.Single(entry => entry.Id == id).Status == PluginStatus.Disabled, $"{id}'s switch stays off.");
            Require(Notice().IsVisible && Notice().Text == expected, $"The notice names the dependency and why: {Notice().Text}");
            Require(chain.All(item => !state.Current.PluginSettings.ContainsKey(item)), $"Refusing {id} writes no setting.");
        }
        window.Close();
        state.Dispose();
        Console.WriteLine("PASS plugins settings: a plugin with a missing or failed dependency is refused with a notice, nothing written");
    }

    // Hosts dialogs in overlays of its own, as the workbench window does.
    private sealed class OverlayWindow : Window, Tereditor.Plugins.UI.Kit.IDialogOwner
    {
        internal Grid Root { get; } = new();
        private int open;

        internal OverlayWindow() => Content = Root;

        public Action Dim() => () => { };

        public Action? Present(Control card, Action dismiss, double width, double height, bool sheet)
        {
            var host = new Tereditor.UI.OverlayHost { ZIndex = 50 + 2 * open++ };
            Root.Children.Add(host);
            host.Show(card, dismiss);
            return () => { host.Hide(); Root.Children.Remove(host); open--; };
        }
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
            var manifests = Tereditor.Host.Core.Plugins.BuiltinPlugins.All.Select(item => item.Manifest).ToArray();
            var panel = new StackPanel();
            // The user wants Air Gateway without an icon anywhere, so it is the one builtin allowed to declare none.
            const string Iconless = "air-gateway";
            var icons = manifests.Where(manifest => manifest.Id != Iconless).Select(manifest =>
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
                Require(manifest.Icon.Length > 0, $"Builtin plugin {manifest.Id} must declare an icon.");
                if (manifest.Icon.StartsWith("asset:", StringComparison.Ordinal))
                    E2eWorkspace.Until(() => { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); return Drawn(icon); });
                else Require(PluginIcons.Glyph(manifest.Icon) != PluginIcons.Fallback || manifest.Icon.Contains("puzzle", StringComparison.Ordinal),
                    $"Builtin plugin {manifest.Id} names icon {manifest.Icon}, which draws the generic puzzle glyph.");
            }
            var github = manifests.Single(manifest => manifest.Id == "github");
            Require(github.Icon == "asset:github.svg" && github.Assets is { } githubAssets && File.Exists(Path.Combine(AppContext.BaseDirectory, "plugins", "github", githubAssets, "github.svg")),
                "GitHub must declare its mark and ship it with the host.");
            var air = manifests.Single(manifest => manifest.Id == Iconless);
            var airMark = PluginIcons.Resolve(air.Icon, null, Ui.Muted);
            Require(air.Icon.Length == 0 && air.Assets is null && !airMark.GetVisualDescendants().OfType<Image>().Any() && airMark is Border { Width: 16, Height: 16 },
                "Air Gateway declares no icon, and its Settings row and Plugins card keep an empty slot of the icon size.");
            foreach (var (manifest, host) in Tereditor.Host.Core.Plugins.BuiltinPlugins.LanguageServerEntries.Where(entry => entry.Host is Tereditor.Plugins.Api.Host.LanguageServerPlugin))
            {
                var server = (Tereditor.Plugins.Api.Host.LanguageServerPlugin)host!;
                var file = Path.Combine(AppContext.BaseDirectory, "plugins", manifest.Id, manifest.Assets ?? "", manifest.Icon["asset:".Length..]);
                Require(server.Spec.Icon is { } mark && File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(mark.Data),
                    $"{manifest.Id}'s plugin icon must be the mark Code Intelligence shows for {server.Spec.Name}.");
            }
            window.Close();
            Console.WriteLine($"PASS plugins settings: all {icons.Length} builtin plugin cards draw an icon, Air Gateway none; language servers show their Code Intelligence marks");
        }
        finally { PluginIcons.ReadAsset = read; }
    }
}