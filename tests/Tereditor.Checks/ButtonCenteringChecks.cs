using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace SharpRail.Checks;

/// <summary>Fixed-height buttons under the application's real styles centre their content vertically without a per-control setting.</summary>
internal static class ButtonCenteringChecks
{
    internal static void RunUi()
    {
        // The Android and Browser connect screens' 40px buttons, which set no VerticalContentAlignment.
        var buttons = new[] { "Connect", "Use address or QR code", "Open sign-in page again" }
            .Select(text => new Button { Content = text, Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center })
            .ToArray();
        var window = new Window { Width = 360, Height = 300, Content = new StackPanel { Spacing = 8, Children = { buttons[0], buttons[1], buttons[2] } } };
        window.Show();
        try
        {
            foreach (var button in buttons)
            {
                var text = button.GetVisualDescendants().OfType<TextBlock>().First();
                var bounds = text.TranslatePoint(default, button) is { } origin ? new Rect(origin, text.Bounds.Size) : throw new InvalidOperationException("Button text is not in the button.");
                // A stretched TextBlock draws its line at its top, so the line is its desired height from the block's top.
                var offset = Math.Abs(bounds.Y + text.DesiredSize.Height / 2 - button.Bounds.Height / 2);
                if (offset > 1) throw new InvalidOperationException($"'{button.Content}' text centre is {offset:0.0}px from the button's vertical centre (text {bounds}, button height {button.Bounds.Height}).");
            }
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Button centering: fixed-height buttons centre their text vertically under the application styles");
    }
}