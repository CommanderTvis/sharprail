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
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => remove();
        var type = this.FindControl<Button>("FrontmatterType")!;
        type.IsVisible = convert is not null;
        type.Content = shape switch { "Sequence" => "☷", "Mapping" => "{}", _ => "T" };
        AutomationProperties.SetName(type, "Change property type");
        if (convert is not null)
        {
            var menu = type.ContextMenu!;
            foreach (var choice in menu.Items.OfType<MenuItem>())
                choice.Click += (_, _) => convert((string)choice.Header!);
            type.Click += (_, _) => menu.Open(type);
        }
    }
}