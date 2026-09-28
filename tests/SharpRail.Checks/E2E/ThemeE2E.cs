using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using SharpRail.UI.Panels;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ThemeE2E
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "theme-reload");
        string original;
        string target;
        using (var app = new E2eWorkspace(directory))
        {
            original = app.Window.Preferences.Theme;
            var settings = Open(app);
            var options = settings.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Name is "Theme_dark" or "Theme_light").ToArray();
            Require(options.Length > 1 && options.Any(button => button.Name == "Theme_" + original),
                "Appearance must expose multiple fixed themes including the active theme.");
            target = options.First(button => button.Name != "Theme_" + original).Name![6..];
            app.Click(options.Single(button => button.Name == "Theme_" + target));
            Require(app.Window.Preferences.Theme == target && app.Window.ActualThemeVariant == Variant(target) &&
                settings.ActualThemeVariant == Variant(target), "Choosing a theme must update the workbench and Settings immediately.");
            Escape(settings);
        }
        using (var reloaded = new E2eWorkspace(directory))
        {
            Require(reloaded.Window.Preferences.Theme == target && reloaded.Window.ActualThemeVariant == Variant(target),
                "A fresh window must restore the selected theme from disk.");
            var settings = Open(reloaded);
            reloaded.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Theme_" + original));
            Require(reloaded.Window.ActualThemeVariant == Variant(original), "The original theme must remain selectable after reload.");
            Escape(settings);
        }
        Console.WriteLine("PASS upstream: appearance switches a discovered theme and persists it across reload");
    }

    private static ThemeVariant Variant(string theme) => theme == "light" ? ThemeVariant.Light : ThemeVariant.Dark;

    private static SettingsWindow Open(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        return app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
    }

    private static void Escape(SettingsWindow settings)
    {
        settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose").Focus();
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !settings.IsVisible);
    }
}
