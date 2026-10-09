using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>Draws a <see cref="ToastQueue"/> as stacked cards, newest last, and ends each toast when its lifetime runs out.</summary>
public sealed class ToastStack : StackPanel
{
    private readonly Dictionary<long, (Border Card, DispatcherTimer? Timer)> cards = [];
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
            var card = Card(toast);
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

    private Border Card(Toast toast)
    {
        var text = new StackPanel { Spacing = 4 };
        if (toast.Title is not null)
        {
            var title = Ui.Text(toast.Title, Ui.TextBrush);
            title.Name = "ToastTitle"; title.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(title);
        }
        var message = Ui.Text(toast.Message);
        message.Name = "ToastMessage"; message.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(message);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,24"), Margin = new Thickness(12) };
        Ui.Place(content, text);
        if (toast.Action is { } action)
        {
            var invoke = new Button
            {
                Name = "ToastAction",
                Content = Ui.Text(action.Label, Ui.Accent),
                Padding = new Thickness(8, 2),
                Margin = new Thickness(8, -2, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Top
            };
            invoke.Click += (_, _) => { queue?.Dismiss(toast.Id); action.Invoke(); };
            Ui.Place(content, invoke, 0, 1);
        }
        var dismiss = new Button
        {
            Name = "ToastDismiss",
            Content = Ui.Icon("close", Ui.Muted, 14),
            Width = 24,
            Height = 24,
            Padding = new Thickness(4),
            Margin = new Thickness(0, -4, -4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(dismiss, "Dismiss");
        dismiss.Click += (_, _) => queue?.Dismiss(toast.Id);
        Ui.Place(content, dismiss, 0, 2);
        var stripe = new Border
        {
            Background = toast.Variant switch { ToastVariant.Error => Ui.Danger, ToastVariant.Success => Ui.Success, _ => Ui.Accent }
        };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*") };
        Ui.Place(layout, stripe); Ui.Place(layout, content, 0, 1);
        var card = new Border
        {
            Name = "Toast",
            Tag = toast,
            Child = layout,
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(0, 1, 1, 1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true
        };
        AutomationProperties.SetLiveSetting(card, toast.Variant == ToastVariant.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        return card;
    }
}