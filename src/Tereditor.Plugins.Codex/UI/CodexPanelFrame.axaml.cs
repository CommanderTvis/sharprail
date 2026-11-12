using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexPanelFrame : DockPanel
{
    internal CodexPanelFrame() => AvaloniaXamlLoader.Load(this);
}