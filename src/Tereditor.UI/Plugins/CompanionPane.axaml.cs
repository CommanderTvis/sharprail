using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Plugins;

internal sealed partial class CompanionPane : UserControl
{
    public CompanionPane(Control content, Action close)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("CompanionContent")!.Content = content;
        var button = this.FindControl<Button>("CompanionClose")!;
        button.Content = Ui.Icon("close");
        button.Click += (_, _) => close();
    }
}