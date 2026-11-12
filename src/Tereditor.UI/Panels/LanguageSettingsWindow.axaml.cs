using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguageSettingsWindow : Window
{
    internal LanguageSettingsWindow(LanguageConfigurationView configuration, string server, string workspace)
    {
        AvaloniaXamlLoader.Load(this);
        Title = LanguageServerNameConverter.Display(server);
        var heading = this.FindControl<TextBlock>("SettingsHeading")!;
        heading.Text = Title;
        heading.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(heading).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        this.FindControl<TextBlock>("SettingsWorkspace")!.Text = workspace;
        var body = this.FindControl<ContentControl>("SettingsBody")!;
        body.Content = configuration;
        Closed += (_, _) => body.Content = null;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
}