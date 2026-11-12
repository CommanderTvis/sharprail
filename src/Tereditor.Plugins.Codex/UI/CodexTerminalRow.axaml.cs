using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.Codex.UI;

internal sealed partial class CodexTerminalRow : StackPanel
{
    internal CodexTerminalRow() => AvaloniaXamlLoader.Load(this);
}