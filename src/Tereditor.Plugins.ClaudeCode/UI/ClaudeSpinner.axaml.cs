using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.ClaudeCode.UI;

internal sealed partial class ClaudeSpinner : Border
{
    internal ClaudeSpinner() => AvaloniaXamlLoader.Load(this);
}