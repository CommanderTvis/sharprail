using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexSettingActions : StackPanel
{
    internal CodexSettingActions() => AvaloniaXamlLoader.Load(this);
}