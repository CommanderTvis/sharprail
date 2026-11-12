using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Panels;

internal sealed partial class FileTransferProgress : UserControl
{
    private CancellationTokenSource? cancellation;
    internal bool Busy => cancellation is not null;

    public FileTransferProgress()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CancelTransfer")!.Click += (_, _) => cancellation?.Cancel();
    }

    internal async Task RunAsync(string message, CancellationToken token, Func<CancellationToken, Task> transfer)
    {
        if (Busy) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation = source;
        this.FindControl<TextBlock>("TransferMessage")!.Text = message;
        IsVisible = true;
        try { await transfer(source.Token); }
        finally { cancellation = null; IsVisible = false; }
    }
}