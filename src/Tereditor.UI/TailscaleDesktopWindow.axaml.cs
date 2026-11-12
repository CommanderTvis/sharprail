#if !ANDROID
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI;

internal sealed partial class TailscaleDesktopWindow : Window
{
    public event Action<TailscaleConnection>? Connected;

    public TailscaleDesktopWindow(string statePath)
    {
        RequestedThemeVariant = Ui.Theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        AvaloniaXamlLoader.Load(this);
        var view = new TailscaleConnectView(statePath);
        view.Connected += next => Connected?.Invoke(next);
        this.FindControl<ContentControl>("TailscaleConnectBody")!.Content = view;
    }
}
#endif