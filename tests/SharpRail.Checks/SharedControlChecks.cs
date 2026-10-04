using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class SharedControlChecks
{
    internal static void Run()
    {
        var original = Ui.Theme;
        var checkbox = new CheckBox { Content = "Enabled", IsChecked = true };
        var primary = Ui.Button("Confirm", () => { });
        primary.Classes.Add("primary");
        var selected = new ToggleButton { Content = "Selected", IsChecked = true };
        var window = new Window { Width = 400, Height = 250, Content = new StackPanel { Children = { checkbox, primary, selected } } };
        window.Show();
        try
        {
            foreach (var theme in Themes.All)
            {
                window.MouseMove(new Point(390, 240));
                Ui.Apply(theme);
                Settle(20);
                var glyph = checkbox.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Name == "CheckGlyph");
                var fill = checkbox.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NormalRectangle");
                Require(Contrast(((ISolidColorBrush)glyph.Fill!).Color, ((ISolidColorBrush)fill.Background!).Color) >= 3,
                    $"{theme.Id}: the checked glyph must remain readable against its fill.");
                var presenter = primary.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
                Require(Contrast(((ISolidColorBrush)((TextBlock)primary.Content!).Foreground!).Color,
                    ((ISolidColorBrush)presenter.Background!).Color) >= 4.5,
                    $"{theme.Id}: a primary button's label must use the shared readable foreground.");
                Require(ReferenceEquals(selected.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter").BorderBrush, Ui.Accent),
                    "Shared selected toggles retain an accent outline, independent of hover.");
                window.MouseMove(checkbox.TranslatePoint(new Point(10, 10), window)!.Value);
                Settle(10);
                Require(Contrast(((ISolidColorBrush)glyph.Fill!).Color, ((ISolidColorBrush)fill.Background!).Color) >= 3,
                    $"{theme.Id}: hovering must preserve checked-glyph contrast.");
                primary.IsEnabled = false;
                Settle(10);
                Require(ReferenceEquals(((TextBlock)primary.Content!).Foreground, Ui.ControlDisabledText),
                    "Factory labels follow the shared disabled foreground.");
                primary.IsEnabled = true;
            }
        }
        finally { window.Close(); Ui.Apply(original); }
        Console.WriteLine("PASS shared checkbox glyphs, primary labels and selected toggles across every bundled theme");
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}