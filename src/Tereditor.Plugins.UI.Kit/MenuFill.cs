using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Opens a context menu whose items need off-UI-thread work only once those items are in. A native popup keeps the
/// size it measured when it opened, so items added or changed after opening are clipped.
/// </summary>
public static class MenuFill
{
    private static readonly ConditionalWeakTable<ContextMenu, Action<Control?>> Requests = [];
    // The control a menu was last assigned to: a rebuilt view can hand its menu to the control it keeps.
    private static readonly ConditionalWeakTable<ContextMenu, Control> Holders = [];

    static MenuFill() => Control.ContextMenuProperty.Changed.AddClassHandler<Control>((control, change) =>
    {
        if (change.NewValue is ContextMenu menu) Holders.AddOrUpdate(menu, control);
    });

    /// <summary>
    /// Holds <paramref name="menu"/> closed when it is asked to open, runs <paramref name="gather"/> on the thread pool,
    /// applies its result (or <paramref name="failed"/>) on the UI thread and then opens the menu once on the control
    /// holding it (<paramref name="owner"/> until another takes it). A newer open request, a cancelled
    /// <paramref name="lifetime"/> or a holder that left the visual tree drops the result; a menu with no items stays
    /// closed. A context request goes through this on its own; code that opens the menu itself calls
    /// <see cref="Open"/>, because <see cref="ContextMenu.Open(Control?)"/>
    /// raises no <see cref="ContextMenu.Opening"/>.
    /// </summary>
    public static void FillBeforeOpening<T>(ContextMenu menu, Control owner, Func<CancellationToken, Task<T>> gather, Action<T> apply,
        Action<Exception>? failed = null, CancellationToken lifetime = default)
    {
        var generation = 0;
        var ready = false;
        CancellationTokenSource? pending = null;
        async void Request(Control? target)
        {
            var current = ++generation;
            pending?.Cancel(); pending?.Dispose();
            var load = pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            var holder = target ?? (Holders.TryGetValue(menu, out var held) ? held : owner);
            bool Current() => current == generation && !load.IsCancellationRequested && TopLevel.GetTopLevel(holder) is not null && holder.IsEffectivelyVisible;
            try
            {
                var result = await Task.Run(() => gather(load.Token), load.Token);
                if (!Current()) return;
                apply(result);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                if (!Current()) return;
                failed?.Invoke(error);
            }
            // A rebuilt view handed the holder a fresh filled menu while this one filled: the request opens that one instead.
            if (holder.ContextMenu is { } replacement && !ReferenceEquals(replacement, menu) && Requests.TryGetValue(replacement, out var forward) &&
                Holders.TryGetValue(replacement, out var replacementHolder) && ReferenceEquals(replacementHolder, holder))
            {
                forward(holder);
                return;
            }
            if (menu.Items.Count == 0 || menu.IsOpen) return;
            ready = true;
            try { menu.Open(holder); }
            finally { ready = false; }
        }
        if (ReferenceEquals(owner.ContextMenu, menu)) Holders.AddOrUpdate(menu, owner);
        Requests.AddOrUpdate(menu, Request);
        menu.Opening += (_, e) =>
        {
            if (ready) { e.Cancel = false; return; }
            e.Cancel = true;
            Request(null);
        };
    }

    /// <summary>Opens <paramref name="menu"/> on <paramref name="target"/>: after its fill when it has one, at once otherwise.</summary>
    public static void Open(ContextMenu menu, Control target)
    {
        if (Requests.TryGetValue(menu, out var request)) request(target);
        else menu.Open(target);
    }
}