using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

public sealed partial class DialogWindow : Window
{
    public DialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape || AppCommands.IsClose(e)) { Close(); e.Handled = true; } };
        Opened += (_, _) =>
        {
            this.FindControl<Border>("DialogCard")!.Effect =
                new DropShadowEffect { OffsetX = 0, OffsetY = 8, BlurRadius = 28, Opacity = 1, Color = Ui.DialogShadow.Color };
            // An empty field area would still add a gap between the text and the actions.
            foreach (var button in this.FindControl<StackPanel>("DialogActions")!.Children.OfType<Button>())
            {
                // Dialog buttons follow the card styles: outline in the text colour, or the solid primary.
                button.ClearValue(Button.PaddingProperty);
                var primary = button.Classes.Contains("primary");
                if (primary) { button.ClearValue(Button.BackgroundProperty); button.ClearValue(Button.BorderBrushProperty); button.ClearValue(Button.BorderThicknessProperty); }
                if (button.Content is TextBlock label) label.Foreground = primary ? Ui.OnPrimary : Ui.TextBrush;
            }
            var fields = this.FindControl<StackPanel>("DialogFields")!;
            fields.IsVisible = fields.IsVisible && fields.Children.Count > 0;
            if (Owner is WorkbenchWindow workbench)
            {
                var undim = workbench.Dim();
                Closed += (_, _) => undim();
            }
        };
    }
}