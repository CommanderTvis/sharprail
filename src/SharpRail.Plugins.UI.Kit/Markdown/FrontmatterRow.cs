using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class FrontmatterRow : UserControl
{
    internal FrontmatterRow(Control key, Control value, Action remove, string label, Action<string>? convert = null, string shape = "Text")
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("Key")!.Content = key;
        this.FindControl<ContentControl>("Value")!.Content = value;
        var button = this.FindControl<Button>("Remove")!;
        button.Content = ActionIcons.Create(ActionIcon.Close, Ui.Muted, 10);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => remove();
        if (convert is null) { ContextMenu = null; return; }
        foreach (var choice in ContextMenu!.Items.OfType<MenuItem>())
        {
            choice.IsVisible = (string)choice.Tag! != shape;
            choice.Click += (_, _) => convert((string)choice.Tag!);
        }
    }
}