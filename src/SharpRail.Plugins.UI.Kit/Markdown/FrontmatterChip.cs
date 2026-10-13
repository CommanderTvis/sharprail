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
        AutomationProperties.SetName(button, "Remove " + value);
        button.Click += (_, _) => remove();
    }
}