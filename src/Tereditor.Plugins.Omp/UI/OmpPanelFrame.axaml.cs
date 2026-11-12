using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.Omp.UI;

internal sealed partial class OmpPanelFrame : DockPanel
{
    internal OmpPanelFrame() => AvaloniaXamlLoader.Load(this);
}