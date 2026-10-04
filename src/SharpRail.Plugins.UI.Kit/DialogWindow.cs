using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
namespace SharpRail.Plugins.UI.Kit;

/// <summary>A window that dims itself while a dialog it owns is open.</summary>
public interface IDialogOwner
{
    /// <summary>Dims the window behind a modal; the returned action removes it.</summary>
    Action Dim();
}

public sealed partial class DialogWindow : Window
{
    public DialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape || CommandKeys.IsClose(e)) { Close(); e.Handled = true; } };
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
            }
            var fields = this.FindControl<StackPanel>("DialogFields")!;
            fields.IsVisible = fields.IsVisible && fields.Children.Count > 0;
            if (Owner is IDialogOwner workbench)
            {
                var undim = workbench.Dim();
                Closed += (_, _) => undim();
            }
        };
    }

    /// <summary>A dialog card titled <paramref name="title"/>; standard dialogs use the reference's 28rem card.</summary>
    public static DialogWindow Create(string title, double width)
    {
        // The window adds room for the card's shadow.
        var card = width <= 520 ? Math.Min(width, 448) : width;
        // The height fits the content; callers that need a fixed size (the Mermaid viewer) set it themselves.
        var window = new DialogWindow { Title = title, Width = (card + 48) * InterfaceZoom.Current };
        window.FindControl<TextBlock>("DialogHeading")!.Text = title;
        return window;
    }
}