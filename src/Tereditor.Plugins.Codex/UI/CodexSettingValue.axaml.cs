using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexSettingValue : Panel
{
    internal CodexSettingValue() => AvaloniaXamlLoader.Load(this);
}