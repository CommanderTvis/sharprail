using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Panels;

public sealed partial class DialogWindow : Window
{
    public DialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
}
