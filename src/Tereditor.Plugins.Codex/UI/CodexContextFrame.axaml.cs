using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexContextFrame : StackPanel
{
    internal CodexContextFrame() => AvaloniaXamlLoader.Load(this);
}