using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>The kit's control styles, compiled with their XAML and added to the application once by <see cref="Ui.ApplyResources"/>.</summary>
internal sealed partial class ControlStyles : Styles
{
    public ControlStyles() => AvaloniaXamlLoader.Load(this);
}