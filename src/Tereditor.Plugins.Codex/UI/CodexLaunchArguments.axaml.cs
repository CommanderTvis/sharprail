using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexLaunchArguments : UserControl
{
    public CodexLaunchArguments() => AvaloniaXamlLoader.Load(this);
}