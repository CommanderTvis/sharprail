using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.ClaudeCode.UI;

internal sealed partial class ClaudeLspCapability : Border
{
    internal ClaudeLspCapability() => AvaloniaXamlLoader.Load(this);
}