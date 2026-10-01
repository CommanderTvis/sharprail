using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ThemeE2E
{
    internal static void Run(string root)
    {
        Catalogue();
        PersistsAcrossReload(root);
        SystemMode(root);
        SystemModeAcrossClients(root);
        HighContrastTabs(root);
        LegacyProfiles(root);
        MarkdownSurfaces(root);
    }

    private static void Catalogue()
    {
        var ids = Themes.All.Select(theme => theme.Id).ToArray();
        Require(ids.SequenceEqual(["dark", "light", "high-contrast-dark", "high-contrast-light"]),
            "The catalogue must mirror the reference's bundled manifests in manifest order.");
        var dark = Themes.Exact("dark")!;
        var contrast = Themes.Exact("high-contrast-light")!;
        Require(dark.Label == "Dark" && dark["content"] == Color.Parse("#18181b") && dark["selection"] == Color.FromArgb(0x26, 0x6a, 0xc8, 0xff) &&
            dark.Colors["selectionForeground"] is null && dark.Ansi.Count == 16 && dark.Ansi[1] == Color.Parse("#cd3131"),
            "Manifest colours, eight-digit alpha and nullable foregrounds must parse exactly.");
        Require(contrast.Label == "High Contrast Light" && contrast.IsLight && contrast.IsHighContrast &&
            contrast["selectionForeground"] == Colors.White && contrast["accent"] == Color.Parse("#125c19"),
            "High-contrast manifests must carry their appearance, contrast and selected-text foreground.");
        var pair = Themes.DerivePair("high-contrast-dark");
        Require(pair.Dark == "high-contrast-dark" && pair.Light == "high-contrast-light" &&
            Themes.DerivePair("light") is { Light: "light", Dark: "dark" } && Themes.DerivePair("missing") is { Light: "light", Dark: "dark" },
            "System pairs must fill the fixed theme's slot and match its contrast on the other side.");
        var wrongSlot = new Preferences { ThemeMode = "system", SystemThemePair = new() { Light = "dark", Dark = "gone" } };
        Require(Themes.Resolve(wrongSlot, "light") is { Fallback: true, Theme.Id: "light" } && Themes.Resolve(wrongSlot, "dark") is { Fallback: true, Theme.Id: "dark" },
            "An unavailable or wrong-appearance slot must fall back to the lowest-order normal theme of that appearance.");
        Console.WriteLine("PASS bundled theme catalogue mirrors the reference manifests, pair derivation and fallback");
    }

    private static void PersistsAcrossReload(string root)
    {
        var directory = Path.Combine(root, "theme-reload");
        string original;
        string target;
        using (var app = new E2eWorkspace(directory))
        {
            var settings = Open(app);
            var options = Options(settings);
            Require(options.Length == Themes.All.Count && options.Length > 1, "Appearance must expose every discovered theme.");
            original = Active(settings).Id;
            Require(app.Window.Preferences.Theme == original && Ui.Theme.Id == original, "The active option must be the applied theme.");
            target = options.Select(option => (ThemeManifest)option.Tag!).First(theme => theme.Id != original).Id;
            app.Click(Option(settings, "Theme_" + target));
            Require(app.Window.Preferences.Theme == target && Ui.Theme.Id == target && app.Window.ActualThemeVariant == Variant(target) &&
                settings.ActualThemeVariant == Variant(target), "Choosing a theme must update the workbench and Settings immediately.");
            Escape(settings);
        }
        using (var reloaded = new E2eWorkspace(directory))
        {
            Require(reloaded.Window.Preferences.Theme == target && Ui.Theme.Id == target && reloaded.Window.ActualThemeVariant == Variant(target),
                "A fresh window must restore the selected theme from disk.");
            var settings = Open(reloaded);
            reloaded.Click(Option(settings, "Theme_" + original));
            Require(Ui.Theme.Id == original && reloaded.Window.ActualThemeVariant == Variant(original), "The original theme must remain selectable after reload.");
            Escape(settings);
        }
        Console.WriteLine("PASS upstream: appearance switches a discovered theme and persists it across reload");
    }

    // One client and its persisted pair; SystemModeAcrossClients covers the peer.
    private static void SystemMode(string root)
    {
        var application = Application.Current!;
        var device = application.RequestedThemeVariant;
        var directory = Path.Combine(root, "theme-system");
        try
        {
            application.RequestedThemeVariant = ThemeVariant.Light;
            ThemeManifest alternateLight;
            ThemeManifest fixedTheme;
            using (var app = new E2eWorkspace(directory))
            {
                var settings = Open(app);
                fixedTheme = Active(settings);
                var initialLight = Themes.All.First(theme => theme.IsLight && theme.Contrast == fixedTheme.Contrast);
                alternateLight = Themes.All.First(theme => theme.IsLight && theme != initialLight);

                app.Click(Option(settings, "ThemeMode_system"));
                Until(() => Ui.Theme == initialLight);
                Require(!Options(settings).Any(option => option.IsEffectivelyVisible), "Match system must replace the fixed list with the pair selectors.");
                Require(CurrentText(settings).StartsWith("Light", StringComparison.Ordinal), "Settings must show the device's light appearance.");
                var trigger = Option(settings, "SystemTheme_light_trigger");
                Require(AutomationProperties.GetName(trigger) == $"Light theme: {initialLight.Label}", "The light trigger must name its theme.");
                app.Click(trigger);
                Until(() => trigger.ContextMenu!.IsOpen);
                var menu = trigger.ContextMenu!;
                Require(menu.Items.OfType<MenuItem>().Select(item => item.Name).SequenceEqual(Themes.All.Where(theme => theme.IsLight)
                    .Select(theme => "SystemTheme_light_option_" + theme.Id)), "The light slot must offer exactly the light manifests.");
                app.Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "SystemTheme_light_option_" + alternateLight.Id), freshGesture: false);
                Until(() => Ui.Theme == alternateLight);

                application.RequestedThemeVariant = ThemeVariant.Dark;
                Until(() => Ui.Theme == fixedTheme && CurrentText(settings).StartsWith("Dark", StringComparison.Ordinal));
                Require(AutomationProperties.GetName(Option(settings, "SystemTheme_dark_trigger")) == $"Dark theme: {fixedTheme.Label}",
                    "The fixed theme must fill its own slot of the derived pair.");
                Settle(100);
                using (var frame = settings.CaptureRenderedFrame()!)
                    frame.Save(Path.Combine(Directory.GetCurrentDirectory(), ".bench", "theme-settings-system.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                app.Click(Option(settings, "ThemeMode_fixed"));
                Until(() => Ui.Theme == fixedTheme && app.Window.Preferences.ThemeMode == "fixed");
                application.RequestedThemeVariant = ThemeVariant.Light;
                Settle(100);
                Require(Ui.Theme == fixedTheme, "Fixed mode must ignore the device appearance.");
                app.Click(Option(settings, "ThemeMode_system"));
                Until(() => Ui.Theme == alternateLight);
                Require(app.Window.Preferences.SystemThemePair is { } pair && pair.Light == alternateLight.Id && pair.Dark == fixedTheme.Id,
                    "Returning to Match system must retain the explicit pair.");
                Escape(settings);
            }
            using (var reloaded = new E2eWorkspace(directory))
            {
                Require(reloaded.Window.Preferences.ThemeMode == "system" && Ui.Theme == alternateLight,
                    "A fresh window must restore system mode and its explicit light theme.");
                application.RequestedThemeVariant = ThemeVariant.Dark;
                Until(() => Ui.Theme == fixedTheme);
                var settings = Open(reloaded);
                reloaded.Click(Option(settings, "ThemeMode_fixed"));
                Until(() => reloaded.Window.Preferences.ThemeMode == "fixed");
                Escape(settings);
            }
        }
        finally { application.RequestedThemeVariant = device; }
        Console.WriteLine("PASS upstream: system mode follows each client and retains its explicit pair (single client)");
    }

    /// <summary>
    /// The peer is a second client of a real gRPC host with its own profile. Both clients share one
    /// process and so one device appearance; each resolves the host-synced mode and pair for its own
    /// appearance, which the check evaluates for both slots.
    /// </summary>
    private static void SystemModeAcrossClients(string root)
    {
        var application = Application.Current!;
        var device = application.RequestedThemeVariant;
        var directory = Path.Combine(root, "theme-system-clients");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.md"), "# clients\n");
        const string token = "theme-clients";
        var server = SharpRail.Host.Remote.RemoteServer.Create(directory, System.Net.IPAddress.Loopback, 0, token);
        Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
        try
        {
            var endpoint = new Uri(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(server.Services)
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single());
            application.RequestedThemeVariant = ThemeVariant.Light;
            using var app = new E2eWorkspace(endpoint, token, directory, directory + "-page-profile", directory);
            Until(() => app.Window.WorkspaceMounted);
            var settings = Open(app);
            var fixedTheme = Active(settings);
            var initialLight = Themes.All.First(theme => theme.IsLight && theme.Contrast == fixedTheme.Contrast);
            var alternateLight = Themes.All.First(theme => theme.IsLight && theme != initialLight);
            app.Click(Option(settings, "ThemeMode_system"));
            Until(() => Ui.Theme == initialLight && CurrentText(settings).StartsWith("Light", StringComparison.Ordinal));
            var trigger = Option(settings, "SystemTheme_light_trigger");
            app.Click(trigger);
            Until(() => trigger.ContextMenu!.IsOpen);
            app.Click(trigger.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "SystemTheme_light_option_" + alternateLight.Id), freshGesture: false);
            Until(() => Ui.Theme == alternateLight);
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Until(() => Ui.Theme == fixedTheme && CurrentText(settings).StartsWith("Dark", StringComparison.Ordinal));

            using var peer = new E2eWorkspace(endpoint, token, directory, directory + "-peer-profile", directory);
            Until(() => peer.Window.WorkspaceMounted);
            var shared = peer.Window.Preferences;
            Require(!ReferenceEquals(shared, app.Window.Preferences), "The peer must hold its own copy of the settings.");
            Until(() => shared.ThemeMode == "system" && shared.SystemThemePair is { } pair && pair.Light == alternateLight.Id && pair.Dark == fixedTheme.Id);
            Require(Themes.Resolve(shared, "light").Theme == alternateLight && Themes.Resolve(shared, "dark").Theme == fixedTheme,
                "A light-appearance peer shows the explicit light theme while this dark client shows the fixed theme.");

            app.Click(Option(settings, "ThemeMode_fixed"));
            Until(() => Ui.Theme == fixedTheme && shared.ThemeMode == "fixed");
            Require(Themes.Resolve(shared, "light").Theme == fixedTheme, "Fixed mode reaches the peer.");
            app.Click(Option(settings, "ThemeMode_system"));
            Until(() => shared.ThemeMode == "system");
            Require(Ui.Theme == fixedTheme && Themes.Resolve(shared, "light").Theme == alternateLight,
                "Returning to Match system retains the explicit pair for every client.");
            app.Click(Option(settings, "ThemeMode_fixed"));
            Until(() => shared.ThemeMode == "fixed");
            Escape(settings);
        }
        finally
        {
            application.RequestedThemeVariant = device;
            Task.Run(() => server.StopAsync()).GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS upstream: system mode follows each client and retains its explicit pair (second gRPC client)");
    }

    private static void HighContrastTabs(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "theme-contrast-tabs"));
        var settings = Open(app);
        var original = Active(settings);
        var highContrast = Options(settings).Select(option => (ThemeManifest)option.Tag!).First(theme => theme.IsHighContrast);
        app.Click(Option(settings, "Theme_" + highContrast.Id));
        Until(() => Ui.Theme == highContrast);
        Escape(settings);

        app.Open("README.md", true);
        app.Open("notes.txt");
        Require(app.Tabs.Count(tab => tab.Path is "README.md" or "notes.txt") == 2, "Two editor tabs must be open.");
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        // A workspace may already hold its initial terminal; add two more alongside it.
        var initial = app.Window.Layout.Tabs(bottom.Id).Count(tab => tab.Kind == "terminal");
        for (var count = 0; count < 2; count++)
        {
            app.Click(app.Find<Button>("NewTerminal_" + bottom.Id));
            var expected = initial + count + 1;
            Until(() => app.Window.Layout.Tabs(bottom.Id).Count(tab => tab.Kind == "terminal") == expected);
        }
        app.Window.MouseMove(new Point(app.Window.Bounds.Width - 2, app.Window.Bounds.Height - 2));
        Settle();

        var right = app.Window.Layout.State.Groups.First(group => group.Region == "right" && app.Window.Layout.Selected(group.Id) is not null);
        using var frame = app.Window.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(Directory.GetCurrentDirectory(), ".bench", "theme-contrast-tabs.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        foreach (var group in new[] { app.Center, right.Id, bottom.Id })
        {
            var chrome = app.Window.Layout.Tabs(group).Select(tab => app.Find<Grid>("DockTab_" + tab.Id.Replace(':', '_').Replace('/', '_'))).ToArray();
            var selected = chrome.Where(tab => Marker(tab).IsVisible).ToArray();
            Require(selected.Length == 1, $"Group {group} must show exactly one selected tab.");
            var tab = selected[0];
            var marker = Marker(tab);
            var fill = ((ISolidColorBrush)tab.Children.OfType<Border>().First().Background!).Color;
            Require(fill == highContrast["hover"] && ((ISolidColorBrush)marker.Background!).Color == highContrast["accent"] && marker.Bounds.Height == 2,
                "A selected tab must keep the selected-control surface and a 2px primary edge marker.");
            var scale = app.Window.RenderScaling;
            var middle = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, 4), app.Window)!.Value;
            var edge = marker.TranslatePoint(new Point(marker.Bounds.Width / 2, 1), app.Window)!.Value;
            Require(Pixel(frame, middle, scale) == highContrast["hover"] && Pixel(frame, edge, scale) == highContrast["accent"],
                $"The selected tab in {group} must paint its surface and edge marker.");
        }
        settings = Open(app);
        app.Click(Option(settings, "Theme_" + original.Id));
        Escape(settings);
        Console.WriteLine("PASS upstream: selected workspace tabs keep their surface and edge marker in high contrast");
    }

    private static void LegacyProfiles(string root)
    {
        var directory = Path.Combine(root, "theme-legacy");
        Directory.CreateDirectory(directory);
        foreach (var (stored, theme, mode) in new[] { ("dark", "dark", "fixed"), ("light", "light", "fixed"), ("system", "dark", "system"), ("", "dark", "fixed") })
        {
            File.WriteAllText(Path.Combine(directory, "profile.json"), $$$"""{"Preferences":{"Theme":"{{{stored}}}"}}""");
            var migrated = new ProfileStore(directory).Data.Preferences;
            Require(migrated.Theme == theme && migrated.ThemeMode == mode &&
                (mode == "fixed" ? migrated.SystemThemePair is null : migrated.SystemThemePair is { Light: "light", Dark: "dark" }),
                $"A legacy \"{stored}\" theme must migrate to {theme}/{mode}.");
        }
        File.WriteAllText(Path.Combine(directory, "profile.json"), """{"Preferences":{"Theme":"future-theme","ThemeMode":"system"}}""");
        var opaque = new ProfileStore(directory).Data.Preferences;
        Require(opaque.Theme == "future-theme" && opaque.ThemeMode == "fixed" && Themes.Resolve(opaque, "light") is { Fallback: true, Theme.Id: "dark" },
            "An unknown theme id must stay opaque, resolve to the default, and system mode needs a pair.");
        Console.WriteLine("PASS legacy dark/light/system profiles migrate to theme ids, mode and pair");
    }

    private static void MarkdownSurfaces(string root)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP macOS Mermaid theme surfaces"); return; }
        using var app = new E2eWorkspace(Path.Combine(root, "theme-markdown"));
        var original = Ui.Theme;
        app.Open("DIAGRAM.md", true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Image Diagram() => preview.GetLogicalDescendants().OfType<Image>().Single(image => image.Name == "MermaidDiagram");
        Until(() => preview.GetLogicalDescendants().OfType<Image>().Any(image => image.Name == "MermaidDiagram"));
        foreach (var theme in Themes.All.Append(original))
        {
            if (theme == Ui.Theme) continue;
            // A device appearance change swaps the theme without Settings rebuilding the mounted preview.
            var before = Diagram().Source;
            Ui.Apply(theme);
            Until(() => !ReferenceEquals(Diagram().Source, before));
            Settle(100);
            using (var frame = app.Window.CaptureRenderedFrame()!)
                frame.Save(Path.Combine(Directory.GetCurrentDirectory(), ".bench", $"theme-{theme.Id}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Require(Ui.InfoWash.Color == Ui.Alpha(theme["info"], 12) && Ui.PreviewSelection.Color == Ui.Alpha(theme["selection"], 40),
                $"Markdown washes and preview selection must follow {theme.Id}.");
        }
        Console.WriteLine("PASS Mermaid diagrams and Markdown tints re-theme under every bundled manifest");
    }

    private static Border Marker(Grid tab) => tab.Children.OfType<Border>().Single(border => border.Height == 2);

    private static Color Pixel(Avalonia.Media.Imaging.Bitmap frame, Point at, double scale)
    {
        var pixel = new byte[4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect((int)(at.X * scale), (int)(at.Y * scale), 1, 1), pinned.AddrOfPinnedObject(), 4, 4); }
        finally { pinned.Free(); }
        return frame.Format == Avalonia.Platform.PixelFormats.Bgra8888
            ? Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0])
            : Color.FromArgb(pixel[3], pixel[0], pixel[1], pixel[2]);
    }

    private static ThemeVariant Variant(string theme) => Themes.Resolve(theme).IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

    private static Button[] Options(SettingsWindow settings) =>
        [.. settings.GetLogicalDescendants().OfType<Button>().Where(button => button.Tag is ThemeManifest)];

    private static ThemeManifest Active(SettingsWindow settings) =>
        (ThemeManifest)Options(settings).Single(option => ReferenceEquals(option.Background, Ui.PrimarySubtle)).Tag!;

    private static Button Option(SettingsWindow settings, string name)
    {
        Button? found = null;
        Until(() => (found = settings.GetLogicalDescendants().OfType<Button>().SingleOrDefault(button => button.Name == name && button.IsEffectivelyVisible)) is not null);
        return found!;
    }

    private static string CurrentText(SettingsWindow settings) => string.Join(" ", settings.GetLogicalDescendants().OfType<StackPanel>()
        .Single(panel => panel.Name == "SystemThemeCurrentValue").Children.OfType<TextBlock>().Select(text => text.Text));

    private static SettingsWindow Open(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        return app.Window.OwnedWindows.OfType<SettingsWindow>().Single(window => window.IsVisible);
    }

    private static void Escape(SettingsWindow settings)
    {
        settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose").Focus();
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !settings.IsVisible);
    }
}