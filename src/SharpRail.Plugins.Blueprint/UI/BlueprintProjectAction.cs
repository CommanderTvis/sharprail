using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintProjectAction : UserControl
{
    public BlueprintProjectAction(Action<Control> start)
    {
        AvaloniaXamlLoader.Load(this);
        var button = this.FindControl<Button>("ProjectDraftBlueprint")!;
        button.Background = Ui.Sidebar; button.BorderBrush = Ui.BorderBrush;
        this.FindControl<ContentControl>("Icon")!.Content = Ui.Icon("pencilRuler", Ui.Muted, 24);
        var badge = this.FindControl<Border>("Badge")!;
        badge.Background = Ui.PrimarySubtle; badge.BorderBrush = Ui.PrimaryMuted;
        this.FindControl<TextBlock>("BadgeText")!.Foreground = Ui.Accent;
        this.FindControl<TextBlock>("Title")!.Foreground = Ui.TextBrush;
        this.FindControl<TextBlock>("Description")!.Foreground = Ui.Muted;
        button.Click += (_, _) => start(button);
    }
}