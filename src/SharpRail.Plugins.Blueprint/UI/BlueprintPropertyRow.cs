using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintPropertyRow : UserControl
{
    public BlueprintPropertyRow() => AvaloniaXamlLoader.Load(this);
}