using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Panels;

public sealed partial class LocalBrowserApprovalFields : UserControl
{
    public LocalBrowserApprovalFields() => AvaloniaXamlLoader.Load(this);

#if !ANDROID && !BROWSER
    internal static async Task<bool> Confirm(Workbench workbench, string origin, CancellationToken cancellationToken)
    {
        var completion = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || workbench.ActiveWindow is not { IsVisible: true } owner) return Task.FromResult(false);
            var window = Dialogs.Create("Allow local browser connection?", 520);
            window.Tag = "LocalBrowserApproval";
            var fields = new LocalBrowserApprovalFields();
            fields.FindControl<SelectableTextBlock>("LocalBrowserOrigin")!.Text = origin;
            window.FindControl<StackPanel>("DialogFields")!.Children.Add(fields);
            window.FindControl<StackPanel>("DialogActions")!.IsVisible = false;
            fields.FindControl<Button>("LocalBrowserDeny")!.Click += (_, _) => window.Close(false);
            fields.FindControl<Button>("LocalBrowserAllow")!.Click += (_, _) => window.Close(!cancellationToken.IsCancellationRequested);
            window.Opened += (_, _) => fields.FindControl<Button>("LocalBrowserDeny")!.Focus();
            var result = window.ShowDialog<bool>(owner.OwnedWindows.LastOrDefault(child => child.IsVisible) ?? owner);
            return Wait();
            async Task<bool> Wait()
            {
                using var registration = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Close(false)));
                return await result.WaitAsync(cancellationToken);
            }
        });
        return await completion.WaitAsync(cancellationToken);
    }
#endif
}