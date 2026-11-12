using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Checks;

internal sealed partial class SwitchFixture : StackPanel
{
    internal List<bool> Changes { get; } = [];
    internal SwitchFixture() => AvaloniaXamlLoader.Load(this);
    private void Changed(bool value) => Changes.Add(value);
}