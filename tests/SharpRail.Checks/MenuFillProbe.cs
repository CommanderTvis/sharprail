using Avalonia.Controls;

namespace SharpRail.Checks;

/// <summary>
/// Records a context menu's items when it opens: a native popup keeps the size it measured then, so a menu whose
/// items are added or changed afterwards clips them. Each menu that gathers items off the UI thread is checked this way.
/// </summary>
internal sealed class MenuFillProbe
{
    private ContextMenu menu;
    private readonly Control? owner;
    private readonly string label;
    private string? opened;
    private ContextMenu? openedMenu;

    private MenuFillProbe(ContextMenu menu, Control? owner, string label)
    {
        this.menu = menu;
        this.owner = owner;
        this.label = label;
        menu.Opened += Record;
        if (owner is not null) owner.PropertyChanged += Reassigned;
    }

    internal static MenuFillProbe Watch(ContextMenu menu, string label) => new(menu, null, label);

    /// <summary>Watches whichever menu <paramref name="owner"/> holds: a rebuilt view may hand it a fresh one while it fills.</summary>
    internal static MenuFillProbe Watch(Control owner, string label) => new(owner.ContextMenu!, owner, label);

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
        if (owner is not null) owner.PropertyChanged -= Reassigned;
        var now = Snapshot(menu.Items);
        if (opened is null) throw new InvalidOperationException($"The {label} never opened.");
        if (!ReferenceEquals(openedMenu, menu)) throw new InvalidOperationException($"The {label} opened a menu its button no longer holds.");
        if (opened != now)
            throw new InvalidOperationException($"The {label} opened with [{opened}] and changed to [{now}] afterwards, so its native popup clipped them.");
    }

    private void Record(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { openedMenu = menu; opened = Snapshot(menu.Items); }

    private void Reassigned(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Control.ContextMenuProperty || e.NewValue is not ContextMenu next || ReferenceEquals(next, menu)) return;
        menu.Opened -= Record;
        menu = next;
        menu.Opened += Record;
    }

    private static string Header(object? header) => header switch
    {
        string text => text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(text => text.Text)),
        _ => header?.GetType().Name ?? ""
    };
}