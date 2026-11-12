using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.UI.Panels;

internal sealed partial class FilesPaneFrame : Grid
{
    public FilesPaneFrame() => AvaloniaXamlLoader.Load(this);
}