using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>Draws a <see cref="ToastQueue"/> as stacked <see cref="ToastCard"/>s, newest last, and ends each toast when its lifetime runs out.</summary>
public sealed class ToastStack : StackPanel
{
    private readonly Dictionary<long, (ToastCard Card, DispatcherTimer? Timer)> cards = [];
    private ToastQueue? queue;

    public ToastStack() => Spacing = 8;

    public void Attach(ToastQueue toasts)
    {
        queue = toasts;
        toasts.Changed += Sync;
        Ui.ThemeChanged += Shade;
        Sync();
    }

    public void Detach()
    {
        if (queue is null) return;
        queue.Changed -= Sync;
        Ui.ThemeChanged -= Shade;
        foreach (var (_, timer) in cards.Values) timer?.Stop();
        cards.Clear(); Children.Clear(); queue = null;
    }

    // Cards are kept by id, so a push or a dismissal elsewhere in the queue never restarts another toast's timer.
    private void Sync()
    {
        var live = queue!.Items.Select(toast => toast.Id).ToHashSet();
        foreach (var id in cards.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            cards.Remove(id, out var gone);
            gone.Timer?.Stop(); Children.Remove(gone.Card);
        }
        foreach (var toast in queue.Items)
        {
            if (cards.ContainsKey(toast.Id)) continue;
            DispatcherTimer? timer = null;
            if (toast.Lifetime is { } lifetime)
            {
                timer = new DispatcherTimer { Interval = lifetime };
                timer.Tick += (_, _) => queue?.Dismiss(toast.Id);
                timer.Start();
            }
            var card = new ToastCard(toast, queue);
            cards[toast.Id] = (card, timer);
            Children.Add(card);
        }
        Shade();
    }

    private void Shade()
    {
        var shadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 16, Color = Ui.PopoverShadow.Color });
        foreach (var (card, _) in cards.Values) card.BoxShadow = shadow;
    }
}