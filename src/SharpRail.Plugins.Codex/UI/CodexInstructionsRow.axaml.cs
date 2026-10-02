using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexInstructionsRow : Grid
{
    internal CodexInstructionsRow() => AvaloniaXamlLoader.Load(this);
}