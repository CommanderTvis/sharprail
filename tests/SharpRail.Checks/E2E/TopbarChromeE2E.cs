using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class TopbarChromeE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "topbar-chrome"), openFiles: false);
        var header = app.Find<Border>("WindowTitleBar");
        var settingsButton = app.Find<Button>("SettingsButton");
        Require(app.Window.GetLogicalDescendants().OfType<Control>().All(item => item.Name != "UpdateReady"),
            "A build without a native updater must not show an Update action.");
        ExpectUsableHeader(app, header, settingsButton);
        var workbench = app.Find<Grid>("WorkbenchRoot");
        Require(workbench.RowDefinitions[0].Height.Value == 40 && workbench.Children.OfType<Control>().Where(item => item != header && Grid.GetRow(item) == 0).All(item => !item.IsVisible),
            "The workbench must start below the fixed header row.");
        var initial = HeaderColor(header);
        Require(initial == Ui.Header.Color, "The header must paint the themed header token.");

        app.Click(settingsButton);
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        var original = app.Window.Preferences.Theme;
        var opposite = original == "light" ? "dark" : "light";
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Theme_" + opposite));
        Until(() => HeaderColor(header) != initial);
        Require(HeaderColor(header) == Ui.Header.Color && header.Bounds.Height == 40,
            "A theme change must repaint the header with the new token at the same height.");
        foreach (var theme in Themes.All)
        {
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Theme_" + theme.Id));
            Until(() => HeaderColor(header) == theme["header"]);
            Require(header.Bounds.Height == 40, $"The {theme.Id} header must keep its fixed height.");
        }
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Theme_" + original));
        Until(() => HeaderColor(header) == initial);
        CloseSettings(settings);

        app.Window.Width = 390;
        Settle();
        ExpectUsableHeader(app, header, settingsButton);
        app.Click(settingsButton);
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        CloseSettings(app.Window.OwnedWindows.OfType<SettingsWindow>().First(window => window.IsVisible));
        Console.WriteLine("PASS upstream: ordinary browsers have a fixed themed header with zero native insets");
    }

    private static void CloseSettings(SettingsWindow settings)
    {
        settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose").Focus();
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !settings.IsVisible);
    }

    private static Color HeaderColor(Border header) => ((ISolidColorBrush)header.Background!).Color;

    private static void ExpectUsableHeader(E2eWorkspace app, Border header, Button settings)
    {
        app.Window.UpdateLayout();
        var origin = header.TranslatePoint(default, app.Window)!.Value;
        Require(origin == new Point(0, 0) && header.Bounds.Height == 40 && Math.Abs(header.Bounds.Width - app.Window.Bounds.Width) < .5,
            "The header must be a fixed 40px bar spanning the window from its origin.");
        var box = new Rect(settings.TranslatePoint(default, header)!.Value, settings.Bounds.Size);
        Require(box.Left >= 0 && box.Right <= header.Bounds.Width && box.Top >= 0 && box.Bottom <= header.Bounds.Height,
            "Settings must stay fully inside the header.");
    }
}
