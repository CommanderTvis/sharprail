using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

/// <summary>
/// One of Welcome's action cards, as the reference draws them: icon top-left, title and explainer bottom-left. The
/// primary card is the filled call to action; the others are quiet until hovered.
/// </summary>
public sealed partial class WelcomeCard : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public WelcomeCard() : this("", "", "", false) { }

    public WelcomeCard(string icon, string title, string subtitle, bool primary)
    {
        AvaloniaXamlLoader.Load(this);
        Background = primary ? Ui.PrimarySubtle : Ui.Sidebar;
        BorderBrush = primary ? Ui.PrimaryMuted : Ui.BorderBrush;
        Resources["ButtonBackgroundPointerOver"] = primary ? Ui.PrimarySubtle : Ui.Elevated;
        Resources["ButtonBackgroundPressed"] = primary ? Ui.PrimarySubtle : Ui.Elevated;
        Resources["ButtonBorderBrushPointerOver"] = Ui.PrimaryMuted;
        Resources["ButtonBorderBrushPressed"] = Ui.PrimaryMuted;
        this.FindControl<ContentControl>("Icon")!.Content = Ui.Icon(icon, primary ? Ui.Accent : Ui.Muted, 24);
        var heading = this.FindControl<TextBlock>("Title")!;
        heading.Text = title;
        heading.Foreground = Ui.TextBrush;
        var explainer = this.FindControl<TextBlock>("Subtitle")!;
        explainer.Text = subtitle;
        explainer.Foreground = Ui.Muted;
        Avalonia.Automation.AutomationProperties.SetName(this, title);
    }
}