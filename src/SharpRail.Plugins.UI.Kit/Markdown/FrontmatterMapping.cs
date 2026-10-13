using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class FrontmatterMapping : UserControl
{
    internal StackPanel Entries { get; }

    internal FrontmatterMapping(Action add)
    {
        AvaloniaXamlLoader.Load(this);
        Name = "FrontmatterValue";
        Entries = this.FindControl<StackPanel>("Entries")!;
        this.FindControl<Button>("FrontmatterMapAdd")!.Click += (_, _) => add();
    }
}