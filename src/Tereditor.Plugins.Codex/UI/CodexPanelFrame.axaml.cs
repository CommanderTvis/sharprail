using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.Codex.UI;

internal sealed partial class CodexPanelFrame : DockPanel
{
    internal CodexPanelFrame() => AvaloniaXamlLoader.Load(this);
}