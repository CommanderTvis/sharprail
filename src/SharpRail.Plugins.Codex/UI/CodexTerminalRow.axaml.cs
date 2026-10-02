using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexTerminalRow : StackPanel
{
    internal CodexTerminalRow() => AvaloniaXamlLoader.Load(this);
}