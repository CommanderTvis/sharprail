using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.UI.Panels;

public sealed partial class NewProjectFields : UserControl
{
    public NewProjectFields() => AvaloniaXamlLoader.Load(this);
}