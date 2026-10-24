using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Omp.UI;

internal sealed partial class OmpPanelFrame : DockPanel
{
    internal OmpPanelFrame() => AvaloniaXamlLoader.Load(this);
}