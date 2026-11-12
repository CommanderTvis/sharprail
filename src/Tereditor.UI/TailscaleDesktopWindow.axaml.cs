#if !ANDROID
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using Tereditor.Plugins.Tailscale;
using Tereditor.Plugins.Tailscale.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI;

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