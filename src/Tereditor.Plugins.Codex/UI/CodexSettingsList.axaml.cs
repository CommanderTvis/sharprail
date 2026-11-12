using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexSettingsList : StackPanel
{
    internal CodexSettingsList() => AvaloniaXamlLoader.Load(this);
}