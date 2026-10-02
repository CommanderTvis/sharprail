using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>Explains the intentionally absent launcher additions in a manually started agent session.</summary>
public sealed partial class ManualLaunchNotice : Border
{
    /// <summary>Creates the notice, initially hidden until a manual launch is detected.</summary>
    public ManualLaunchNotice() => AvaloniaXamlLoader.Load(this);
}