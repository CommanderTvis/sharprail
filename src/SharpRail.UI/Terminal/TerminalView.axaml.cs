using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Terminal;

// A terminal tab's body: starts its session, explains start failures with an in-place retry
// and says when the shell has exited.
public sealed partial class TerminalView : UserControl, IDisposable
{
    private readonly TerminalFactory factory;
    private readonly TerminalLaunch launch;
    private readonly ContentControl body;
    private readonly Control failure;
    private readonly SelectableTextBlock failureText;
    private readonly Button retry;
    private readonly TextBlock exitNotice;
    private int generation;
    private bool disposed;

    public TerminalView(TerminalFactory factory, TerminalLaunch launch)
    {
        this.factory = factory; this.launch = launch;
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("TerminalBody")!;
        failure = this.FindControl<Control>("TerminalStartFailure")!;
        failureText = this.FindControl<SelectableTextBlock>("TerminalStartFailureText")!;
        retry = this.FindControl<Button>("TerminalStartRetry")!;
        exitNotice = this.FindControl<TextBlock>("TerminalExited")!;
        retry.Click += (_, _) => Start(retrying: true);
        Focusable = true;
        Start(retrying: false);
    }

    public ITerminalBackend? Backend { get; private set; }
    public bool IsFailed => failure.IsVisible;
    public bool IsExited => exitNotice.IsVisible;

    public async ValueTask<bool> IsBusyAsync() =>
        Backend is { } backend && !IsExited && !IsFailed && backend.Started.IsCompletedSuccessfully && await backend.IsBusyAsync();

    public void FocusTerminal()
    {
        if (Backend is not null && !IsFailed) Backend.FocusTerminal();
        else if (IsFailed) retry.Focus();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    private async void Start(bool retrying)
    {
        if (disposed) return;
        var current = ++generation;
        Backend?.Dispose(); Backend = null; body.Content = null;
        retry.IsEnabled = false;
        exitNotice.IsVisible = false;
        ITerminalBackend? backend = null;
        try
        {
            backend = factory(launch with { SessionId = Guid.NewGuid().ToString("N") });
            Backend = backend;
            body.Content = backend.View;
            await backend.Started;
            if (current != generation) return;
            failure.IsVisible = false;
            if (retrying) backend.FocusTerminal();
            var code = await backend.Exited;
            if (current != generation) return;
            exitNotice.Text = $"[process exited with code {code}]";
            exitNotice.IsVisible = true;
        }
        catch (Exception error)
        {
            if (current != generation) return;
            backend?.Dispose();
            Backend = null; body.Content = null;
            failureText.Text = error.Message;
            failure.IsVisible = true;
            retry.IsEnabled = true;
            if (retrying) retry.Focus();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; generation++;
        Backend?.Dispose(); Backend = null;
    }
}
