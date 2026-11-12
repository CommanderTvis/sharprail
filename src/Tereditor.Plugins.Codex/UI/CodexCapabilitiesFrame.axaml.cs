using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.Codex.UI;

internal sealed partial class CodexCapabilitiesFrame : StackPanel
{
    internal CodexCapabilitiesFrame() => AvaloniaXamlLoader.Load(this);
}