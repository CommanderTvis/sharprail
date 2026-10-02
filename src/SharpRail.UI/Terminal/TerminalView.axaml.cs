using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Terminal;

// A terminal tab's body: attaches to its host session, explains start failures with an in-place retry,
// offers to take the session back when another window took it over, and says when the shell has exited.
public sealed partial class TerminalView : UserControl, IDisposable
{
    private readonly TerminalFactory factory;
    private readonly TerminalLaunch launch;
    private readonly ContentControl body;
    private readonly Control failure;
    private readonly SelectableTextBlock failureText;
    private readonly Button retry;
    private readonly Control detachedNotice;
    private readonly Button takeBack;
    private readonly TextBlock exitNotice;
    private readonly ContentControl companion;
    private readonly GridSplitter companionSplitter;
    private int generation;
    private bool disposed;
    private bool agentNewline;

    public TerminalView(TerminalFactory factory, TerminalLaunch launch)
    {
        this.factory = factory; this.launch = launch;
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("TerminalBody")!;
        failure = this.FindControl<Control>("TerminalStartFailure")!;
        failureText = this.FindControl<SelectableTextBlock>("TerminalStartFailureText")!;
        retry = this.FindControl<Button>("TerminalStartRetry")!;
        detachedNotice = this.FindControl<Control>("TerminalDetached")!;
        takeBack = this.FindControl<Button>("TerminalTakeBack")!;
        exitNotice = this.FindControl<TextBlock>("TerminalExited")!;
        Accessories = this.FindControl<StackPanel>("TerminalAccessories")!;
        companion = this.FindControl<ContentControl>("TerminalCompanion")!;
        companionSplitter = this.FindControl<GridSplitter>("TerminalCompanionSplitter")!;
        retry.Click += (_, _) => Start(retrying: true);
        takeBack.Click += (_, _) => Start(retrying: true);
        Focusable = true;
        Start(retrying: false);
    }

    public ITerminalBackend? Backend { get; private set; }
    public string SessionId => launch.SessionId;
    public TerminalLaunch Launch => launch;
    /// <summary>Plugin accessory rows shown above the surface.</summary>
    public StackPanel Accessories { get; }

    /// <summary>The embedded companion pane beside the surface; null closes it.</summary>
    public Control? Companion
    {
        get => companion.Content as Control;
        set { companion.Content = value; companion.IsVisible = companionSplitter.IsVisible = value is not null; }
    }

    /// <summary>Types into the shell as if typed; ignored until the shell has started.</summary>
    public void Write(string data)
    {
        if (Backend is { } backend && backend.Started.IsCompletedSuccessfully && !IsExited && !IsDetached) backend.Write(data);
    }

    /// <summary>The surface's screen and scrollback as text, oldest line first.</summary>
    public string ReadScreen() => Backend is { } backend && backend.Started.IsCompletedSuccessfully ? backend.ReadScreen() : "";
    public void SetAgentNewline(bool enabled)
    {
        agentNewline = enabled;
        Backend?.SetAgentNewline(enabled);
    }
    public bool IsFailed => failure.IsVisible;
    public bool IsExited => exitNotice.IsVisible;
    public bool IsDetached => detachedNotice.IsVisible;

    public async ValueTask<bool> IsBusyAsync() =>
        Backend is { } backend && !IsExited && !IsFailed && !IsDetached && backend.Started.IsCompletedSuccessfully && await backend.IsBusyAsync();

    public void FocusTerminal()
    {
        if (IsDetached) takeBack.Focus();
        else if (IsFailed) retry.Focus();
        else Backend?.FocusTerminal();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    public void Restart() => Start(retrying: IsKeyboardFocusWithin);

    // Starting again attaches afresh, which also takes the session back from another window.
    private async void Start(bool retrying)
    {
        if (disposed) return;
        var current = ++generation;
        Backend?.Dispose(); Backend = null; body.Content = null;
        retry.IsEnabled = false; takeBack.IsEnabled = false;
        exitNotice.IsVisible = false;
        ITerminalBackend? backend = null;
        try
        {
            backend = factory(launch);
            Backend = backend;
            body.Content = backend.View;
            await backend.Started;
            if (current != generation) return;
            backend.SetAgentNewline(agentNewline);
            failure.IsVisible = false;
            detachedNotice.IsVisible = false; body.IsVisible = true;
            if (retrying) backend.FocusTerminal();
            await Task.WhenAny(backend.Exited, backend.Detached);
            if (current != generation) return;
            if (backend.Detached.IsCompleted)
            {
                // The surface no longer receives output; hide it rather than show a stale screen as live.
                body.IsVisible = false;
                detachedNotice.IsVisible = true;
                takeBack.IsEnabled = true;
                return;
            }
            var code = await backend.Exited;
            exitNotice.Text = $"[process exited with code {code}]";
            exitNotice.IsVisible = true;
        }
        catch (Exception error)
        {
            if (current != generation) return;
            backend?.Dispose();
            Backend = null; body.Content = null;
            body.IsVisible = true; detachedNotice.IsVisible = false;
            failureText.Text = error.Message;
            failure.IsVisible = true;
            retry.IsEnabled = true;
            if (retrying) retry.Focus();
        }
    }

    // Closing the tab ends the host session; disposing alone only detaches this view from it.
    public void Close()
    {
        if (disposed) return;
        var backend = Backend;
        Dispose();
        if (backend is not null) _ = backend.CloseAsync().AsTask();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; generation++;
        Backend?.Dispose(); Backend = null;
    }
}