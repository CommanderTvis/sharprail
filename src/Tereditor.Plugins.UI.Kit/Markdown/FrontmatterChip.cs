using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class FrontmatterChip : UserControl
{
    internal FrontmatterChip(string value, Action remove)
    {
        AvaloniaXamlLoader.Load(this);
        Name = "FrontmatterListItem";
        this.FindControl<TextBlock>("Label")!.Text = value;
        var button = this.FindControl<Button>("Remove")!;
        button.Content = ActionIcons.Create(ActionIcon.Close, Ui.Muted, 8);
        AutomationProperties.SetName(button, "Remove " + value);
        ToolTip.SetTip(button, "Remove " + value);
        button.Click += (_, _) => remove();
    }
}