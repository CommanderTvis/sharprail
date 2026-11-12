using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.Codex.UI;

internal sealed partial class CodexMcpRow : Border
{
    internal CodexMcpRow() => AvaloniaXamlLoader.Load(this);
}