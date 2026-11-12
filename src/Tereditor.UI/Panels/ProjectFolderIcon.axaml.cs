using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Panels;

internal sealed partial class ProjectFolderIcon : UserControl
{
    internal ProjectFolderIcon(Control folder)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("FolderIcon")!.Content = folder;
    }
}