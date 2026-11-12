using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>The Pi side tool: the Early badge and the version of the Pi this host runs, or why there is none.</summary>
internal static class PiPanel
{
    public static Control Create(IPluginUIContext context)
    {
        var version = Ui.Text("Reading the version…", Ui.Hint, 12);
        version.Name = "PiVersion";
        var badge = new Border
        {
            Background = Ui.WarningWash,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0),
            Child = Ui.Text("Early support", Ui.Warning, 11)
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 4) };
        header.Children.Add(Ui.Text("Pi", Ui.TextBrush, 13));
        header.Children.Add(badge);
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(header);
        var detail = new StackPanel { Margin = new Thickness(8, 0) };
        detail.Children.Add(version);
        body.Children.Add(detail);
        var root = new DockPanel { Name = "PiPanel", Background = Ui.Sidebar };
        root.Children.Add(body);
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var status = await context.RequestAsync(PiContract.Status, new PiNoParams());
                text = status.Installed ? (status.Version is { Length: > 0 } found ? $"Version {found}" : "Version unknown") : status.Detail ?? "Pi is not installed.";
            }
            catch (Exception error) when (error is not OperationCanceledException) { text = "Pi could not be reached."; }
            Dispatcher.UIThread.Post(() => version.Text = text);
        });
        return root;
    }
}