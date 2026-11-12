using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexProblemNotice : Border
{
    internal CodexProblemNotice() => AvaloniaXamlLoader.Load(this);
}