using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SharpRail.UI;

/// <summary>
/// A top layer over the workbench for one in-window surface at a time (Settings): a scrim that blocks the content behind it,
/// Tab confined to the surface, Escape closing it, and focus returned to where it was when the surface goes. It replaces an
/// owned window, whose re-ordering on the macOS native backend made it flicker when the owner was clicked.
/// </summary>
public sealed partial class OverlayHost : UserControl
{
    private readonly ContentControl presenter;
    private Control? content;
    private Func<Size, Size>? fit;
    private Action? close;
    private Action? undim;
    private IInputElement? restore;
    private TopLevel? top;

    /// <summary>Dims the workbench behind the surface and returns the undo; unset leaves the scrim invisible.</summary>
    public Func<Action>? Dimmer { get; set; }

    public OverlayHost()
    {
        AvaloniaXamlLoader.Load(this);
        presenter = this.FindControl<ContentControl>("OverlayContent")!;
        SizeChanged += (_, _) => Fit();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled && close is not null) { close(); e.Handled = true; } };
    }

    public bool IsOpen => content is not null;

    /// <summary>
    /// Shows <paramref name="surface"/>, replacing any current one. <paramref name="fit"/> maps the host's size to the surface's
    /// (it is centred); without it the surface fills the host. <paramref name="close"/> is what Escape does.
    /// </summary>
    public void Show(Control surface, Action close, Func<Size, Size>? fit = null, bool dimmed = true)
    {
        Hide();
        this.close = close; this.fit = fit; content = surface;
        restore = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        undim = dimmed ? Dimmer?.Invoke() : null;
        presenter.HorizontalAlignment = fit is null ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Center;
        presenter.VerticalAlignment = fit is null ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Center;
        presenter.Content = surface;
        IsVisible = true;
        Fit();
        if ((top = TopLevel.GetTopLevel(this)) is not null) top.AddHandler(GotFocusEvent, Refocus, RoutingStrategies.Bubble, handledEventsToo: true);
        Dispatcher.UIThread.Post(() => { if (content == surface) surface.Focus(); }, DispatcherPriority.Input);
    }

    /// <summary>Removes the surface, if any, and gives focus back to what had it before.</summary>
    public void Hide()
    {
        if (content is null) return;
        top?.RemoveHandler(GotFocusEvent, Refocus); top = null;
        content = null; close = null; fit = null;
        presenter.Content = null;
        IsVisible = false;
        undim?.Invoke(); undim = null;
        var back = restore as Control; restore = null;
        if (back is not null) Dispatcher.UIThread.Post(() => { if (back.IsEffectivelyVisible && TopLevel.GetTopLevel(back) is not null) back.Focus(); }, DispatcherPriority.Input);
    }

    // Focus that lands behind the surface (a shortcut, a click on the window chrome) is pulled back in.
    private void Refocus(object? sender, RoutedEventArgs e)
    {
        if (content is null || e.Source is not Visual source || source == content || content.IsVisualAncestorOf(source) || this.IsVisualAncestorOf(source)) return;
        Dispatcher.UIThread.Post(() => content?.Focus(), DispatcherPriority.Input);
    }

    private void Fit()
    {
        if (content is null || fit is null) return;
        var size = fit(Bounds.Size);
        content.Width = size.Width; content.Height = size.Height;
    }
}