using Avalonia.Controls;

namespace SharpRail.Checks;

/// <summary>
/// Records a context menu's items when it opens: a native popup keeps the size it measured then, so a menu whose
/// items are added or changed afterwards clips them. Each menu that gathers items off the UI thread is checked this way.
/// </summary>
internal sealed class MenuFillProbe
{
    private readonly ContextMenu menu;
    private readonly string label;
    private string? opened;

    private MenuFillProbe(ContextMenu menu, string label)
    {
        this.menu = menu;
        this.label = label;
        menu.Opened += Record;
    }

    internal static MenuFillProbe Watch(ContextMenu menu, string label) => new(menu, label);

    /// <summary>The items of a menu, nested ones included, as one comparable string.</summary>
    internal static string Snapshot(ItemCollection items) => string.Join("|", items.Select(item => item switch
    {
        MenuItem entry => $"{entry.Name}:{Header(entry.Header)}:{entry.IsEnabled}:{entry.IsVisible}[{Snapshot(entry.Items)}]",
        Control control => control.GetType().Name + ":" + control.IsVisible,
        _ => item?.ToString() ?? ""
    }));

    /// <summary>Fails unless the menu opened with the items it holds now.</summary>
    internal void RequireOpenedFilled()
    {
        menu.Opened -= Record;
        var now = Snapshot(menu.Items);
        if (opened is null) throw new InvalidOperationException($"The {label} never opened.");
        if (opened != now)
            throw new InvalidOperationException($"The {label} opened with [{opened}] and changed to [{now}] afterwards, so its native popup clipped them.");
    }

    private void Record(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => opened = Snapshot(menu.Items);

    private static string Header(object? header) => header switch
    {
        string text => text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(text => text.Text)),
        _ => header?.GetType().Name ?? ""
    };
}