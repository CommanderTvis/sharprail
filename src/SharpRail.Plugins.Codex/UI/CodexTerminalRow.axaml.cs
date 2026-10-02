using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexTerminalRow : WrapPanel
{
    internal CodexTerminalRow() => AvaloniaXamlLoader.Load(this);
}