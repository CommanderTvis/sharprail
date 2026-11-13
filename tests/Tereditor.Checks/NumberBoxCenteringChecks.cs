using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Panels;

namespace Tereditor.Checks;

/// <summary>Number boxes under the application's real styles centre their value vertically and inset it like a text box.</summary>
internal static class NumberBoxCenteringChecks
{
    internal static void RunUi()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;
        try
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = variant;
                // A stock number box (plugin settings), one built like a Settings row, one like a group limit at a
                // fixed height, and a text box and a combo box for the inset and the height.
                var plain = new NumericUpDown { Value = 13, Minimum = 0, Maximum = 99 };
                var row = new NumericUpDown { Value = 13, Minimum = 10, Maximum = 24, Increment = 1 };
                SettingsView.CompactSteppers(row, 24); SettingsView.FitDigits(row, 24);
                var limit = new NumericUpDown { Value = 4, Minimum = 1, Maximum = 32, Height = 28, MinHeight = 28, FontSize = 13 };
                SettingsView.CompactSteppers(limit, 22); SettingsView.FitDigits(limit, 22);
                var text = new TextBox { Text = "13" };
                var combo = new ComboBox { ItemsSource = new[] { "One" }, SelectedIndex = 0 };
                var window = new Window { Width = 360, Height = 300, FontFamily = Ui.InterfaceFont, Content = new StackPanel { Spacing = 8, Children = { plain, row, limit, text, combo } } };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    var textInset = Value(text).TranslatePoint(default, text)!.Value.X;
                    foreach (var (name, box) in new[] { ("stock", plain), ("settings row", row), ("fixed-height", limit) })
                    {
                        var presenter = Value(box);
                        var origin = presenter.TranslatePoint(default, box)!.Value;
                        // The presenter draws its line at its top, so the line's centre is half its desired height down.
                        var offset = Math.Abs(origin.Y + presenter.DesiredSize.Height / 2 - box.Bounds.Height / 2);
                        Require(offset <= 1, $"{variant} {name} number box: the value's centre is {offset:0.0}px from the field's centre (value top {origin.Y:0.0}, line {presenter.DesiredSize.Height:0.0}, field {box.Bounds.Height:0.0}).");
                        Require(Math.Abs(origin.X - textInset) <= 0.5, $"{variant} {name} number box: the value starts {origin.X:0.0}px in, a text box's {textInset:0.0}px.");
                        var scroll = box.GetVisualDescendants().OfType<ScrollViewer>().First();
                        Require(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"{variant} {name} number box scrolls its value ({scroll.Extent.Height:0.0} in {scroll.Viewport.Height:0.0}px).");
                        Require(presenter.Bounds.Width >= presenter.DesiredSize.Width, $"{variant} {name} number box clips its digits ({presenter.Bounds.Width:0.0} of {presenter.DesiredSize.Width:0.0}px).");
                        foreach (var step in box.GetVisualDescendants().OfType<RepeatButton>().Where(step => step.Name is "PART_IncreaseButton" or "PART_DecreaseButton"))
                        {
                            var top = step.TranslatePoint(default, box)!.Value.Y;
                            Require(top >= -0.5 && top + step.Bounds.Height <= box.Bounds.Height + 0.5, $"{variant} {name}: a spinner button sticks out of its {box.Bounds.Height:0.0}px number box ({top:0.0}+{step.Bounds.Height:0.0}).");
                        }
                    }
                    Require(Math.Abs(limit.Bounds.Height - 28) <= 0.5, $"{variant}: a 28px number box laid out {limit.Bounds.Height:0.0}px high.");
                    foreach (var box in new[] { plain, row })
                        Require(Math.Abs(box.Bounds.Height - text.Bounds.Height) <= 0.5 && Math.Abs(box.Bounds.Height - combo.Bounds.Height) <= 0.5,
                            $"{variant}: a number box is {box.Bounds.Height:0.0}px high, a text box {text.Bounds.Height:0.0}px and a combo box {combo.Bounds.Height:0.0}px.");
                }
                finally { window.Close(); }
            }
        }
        finally { app.RequestedThemeVariant = previous; }
        Console.WriteLine("PASS Number box centering: number boxes centre their value and inset it like a text box in light and dark");
    }

    private static TextPresenter Value(Control field) =>
        field.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() ?? throw new InvalidOperationException("The field has no text presenter.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}