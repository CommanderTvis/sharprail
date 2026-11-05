using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
namespace SharpRail.Plugins.UI.Kit;

/// <summary>A window that dims itself while a dialog it owns is open.</summary>
public interface IDialogOwner
{
    /// <summary>Dims the window behind a modal; the returned action removes it.</summary>
    Action Dim();

    /// <summary>
    /// Shows <paramref name="card"/> inside the window itself, over a dimming scrim, clamped to the window, and returns the
    /// action that removes it; null when this window cannot host one, and the dialog opens as an owned window instead.
    /// <paramref name="dismiss"/> is what Escape does. <paramref name="sheet"/> docks the card to the bottom edge.
    /// </summary>
    Action? Present(Control card, Action dismiss, double width, double height, bool sheet);
}

/// <summary>
/// A dialog card. Shown over a window that can host it (<see cref="IDialogOwner.Present"/>) it is content in that window's
/// overlay, like Settings, so clicking the owner never re-orders windows (when <see cref="InWindow"/>); otherwise it is an
/// owned window. Either way <c>Opened</c>, <c>Closed</c>, <c>Close(result)</c> and <c>ShowDialog&lt;T&gt;</c> behave alike.
/// </summary>
public sealed partial class DialogWindow : Window
{
    private static readonly List<DialogWindow> inOverlay = [];
    private Action? hide;
    private TaskCompletionSource<object?>? completion;
    private Window? host;

    /// <summary>Shows this dialog in its owner's overlay when the owner can host it (the default is an owned window).</summary>
    public bool InWindow { get; set; }

    /// <summary>The card as content of the owner's overlay; null while this is an owned window.</summary>
    public Control? Surface { get; private set; }

    /// <summary>The window hosting this dialog's overlay; null while this is an owned window.</summary>
    public Window? Host => host;

    /// <summary>True while shown in an overlay, where the window itself is never shown.</summary>
    public bool InOverlay => completion is not null;

    /// <summary>The child windows of <paramref name="owner"/> plus the dialogs it hosts in its overlay, oldest first.</summary>
    public static IReadOnlyList<Window> On(Window owner) => [.. owner.OwnedWindows, .. inOverlay.Where(dialog => dialog.host == owner)];

    public new bool IsVisible => InOverlay || base.IsVisible;

    public DialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape || CommandKeys.IsClose(e)) { Close(); e.Handled = true; } };
        Opened += (_, _) =>
        {
            var card = this.FindControl<Border>("DialogCard")!;
            if (OperatingSystem.IsAndroid())
            {
                // On Android a dialog is a sheet across the bottom of the screen: no margin for a shadow, square to the edge.
                card.Margin = default;
                card.CornerRadius = new(12, 12, 0, 0);
                card.BorderThickness = new(0, 1, 0, 0);
            }
            else card.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 8, BlurRadius = 28, Opacity = 1, Color = Ui.DialogShadow.Color };
            // An empty field area would still add a gap between the text and the actions.
            foreach (var button in this.FindControl<StackPanel>("DialogActions")!.Children.OfType<Button>())
            {
                // Dialog buttons follow the card styles: outline in the text colour, or the solid primary.
                button.ClearValue(Button.PaddingProperty);
                var primary = button.Classes.Contains("primary");
                if (primary) { button.ClearValue(Button.BackgroundProperty); button.ClearValue(Button.BorderBrushProperty); button.ClearValue(Button.BorderThicknessProperty); }
            }
            var fields = this.FindControl<StackPanel>("DialogFields")!;
            fields.IsVisible = fields.IsVisible && fields.Children.Count > 0;
            // The browser's embedded window sizes to its content once; a dialog whose content grows afterwards (a typed path, an error) would spill past its card.
            if (OperatingSystem.IsBrowser() && Content is Control root)
                LayoutUpdated += (_, _) => { if (Math.Abs(root.DesiredSize.Height - Bounds.Height) is > 0.5 and < 4000) Height = root.DesiredSize.Height; };
            if (Owner is IDialogOwner workbench)
            {
                var undim = workbench.Dim();
                Closed += (_, _) => undim();
            }
        };
    }

    public new Task ShowDialog(Window owner) => ShowDialog<object?>(owner);

    public new Task<T> ShowDialog<T>(Window owner)
    {
        if (!InWindow || owner is not IDialogOwner presenter) return base.ShowDialog<T>(owner);
        // The card leaves its window and takes the window's styles with it; the owner's zoom already applies around it.
        var root = (Control)Content!;
        var zoom = InterfaceZoom.Current;
        var width = double.IsNaN(Width) ? double.NaN : Width / zoom;
        var height = double.IsNaN(Height) ? double.NaN : Height / zoom;
        Content = null;
        var styles = Styles.ToArray(); Styles.Clear();
        foreach (var style in styles) root.Styles.Add(style);
        root.ClearValue(LayoutTransformControl.LayoutTransformProperty);
        root.KeyDown += (_, e) => { if (e.Key == Key.Escape || CommandKeys.IsClose(e)) { Close(); e.Handled = true; } };
        if (presenter.Present(root, () => Close(), width, height, OperatingSystem.IsAndroid()) is not { } remove)
        {
            root.Styles.Clear(); foreach (var style in styles) Styles.Add(style);
            root.SetValue(LayoutTransformControl.LayoutTransformProperty, null);
            Content = root;
            return base.ShowDialog<T>(owner);
        }
        hide = remove; host = owner; Surface = root;
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        inOverlay.Add(this);
        // After the overlay has taken its own focus, so a handler that focuses a field wins.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (completion is { Task.IsCompleted: false }) OnOpened(EventArgs.Empty); }, Avalonia.Threading.DispatcherPriority.Input);
        return Result<T>(completion.Task);
    }

    private static async Task<T> Result<T>(Task<object?> task) => await task is T value ? value : default!;

    public new void Close() => Close(null);

    public new void Close(object? dialogResult)
    {
        if (completion is not { } pending)
        {
            if (dialogResult is null) base.Close(); else base.Close(dialogResult);
            return;
        }
        if (pending.Task.IsCompleted) return;
        inOverlay.Remove(this);
        hide?.Invoke(); hide = null;
        OnClosed(EventArgs.Empty);
        pending.SetResult(dialogResult);
    }

    /// <summary>A dialog card titled <paramref name="title"/>; standard dialogs use the reference's 28rem card.</summary>
    public static DialogWindow Create(string title, double width)
    {
        // The window adds room for the card's shadow.
        var card = width <= 520 ? Math.Min(width, 448) : width;
        // The height fits the content; callers that need a fixed size (the Mermaid viewer) set it themselves.
        var window = new DialogWindow { Title = title, Width = (card + 48) * InterfaceZoom.Current };
        window.FindControl<TextBlock>("DialogHeading")!.Text = title;
        return window;
    }
}