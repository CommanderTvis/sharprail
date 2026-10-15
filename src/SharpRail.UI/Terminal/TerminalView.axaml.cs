using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SharpRail.UI.Terminal;

// A terminal tab's body: attaches to its host session, explains start failures with an in-place retry, shows a
// terminal another client holds live and read-only with an offer to take it over, or back, and says when the
// shell has exited.
public sealed partial class TerminalView : UserControl, IDisposable
{
    private readonly TerminalFactory factory;
    private readonly TerminalLaunch launch;
    private readonly ContentControl body;
    private readonly Control failure;
    private readonly SelectableTextBlock failureText;
    private readonly Button retry;
    private readonly Control detachedNotice;
    private readonly TextBlock detachedText;
    private readonly Button takeBack;
    private readonly TextBlock exitNotice;
    private readonly Control resumeHint;
    private Action? releaseHint;
    private readonly ContentControl companion;
    private readonly GridSplitter companionSplitter;
    // 45% of the view beside a 1* terminal column.
    private GridLength companionWidth = new(45.0 / 55, GridUnitType.Star);
    private int generation;
    private bool disposed;
    private bool agentNewline;
    // This view held the terminal until another client took it.
    private bool displaced;

    public TerminalView(TerminalFactory factory, TerminalLaunch launch)
    {
        this.factory = factory; this.launch = launch;
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("TerminalBody")!;
        failure = this.FindControl<Control>("TerminalStartFailure")!;
        failureText = this.FindControl<SelectableTextBlock>("TerminalStartFailureText")!;
        retry = this.FindControl<Button>("TerminalStartRetry")!;
        detachedNotice = this.FindControl<Control>("TerminalDetached")!;
        detachedText = this.FindControl<TextBlock>("TerminalDetachedText")!;
        takeBack = this.FindControl<Button>("TerminalTakeBack")!;
        exitNotice = this.FindControl<TextBlock>("TerminalExited")!;
        resumeHint = this.FindControl<Control>("TerminalResumeHint")!;
        Accessories = this.FindControl<StackPanel>("TerminalAccessories")!;
        companion = this.FindControl<ContentControl>("TerminalCompanion")!;
        companionSplitter = this.FindControl<GridSplitter>("TerminalCompanionSplitter")!;
        retry.Click += (_, _) => Start(retrying: true);
        takeBack.Click += (_, _) => Start(retrying: true, takeOver: true);
        Focusable = true;
        Start(retrying: false);
    }

    internal bool FileDropWired { get; set; }
    internal bool FileDropActive
    {
        set => this.FindControl<Border>("FileDropHighlight")!.IsVisible = value;
    }

    public ITerminalBackend? Backend { get; private set; }
    public string SessionId => launch.SessionId;
    public TerminalLaunch Launch => launch;
    /// <summary>Plugin accessory rows shown below the surface.</summary>
    public StackPanel Accessories { get; }

    /// <summary>The embedded companion pane beside the surface; null closes it.</summary>
    public Control? Companion
    {
        get => companion.Content as Control;
        set
        {
            var visible = value is not null;
            if (visible != companion.IsVisible)
            {
                var columns = ((Grid)companion.Parent!).ColumnDefinitions;
                var column = columns[2];
                // The first opening takes 45%; afterwards the width the user left is restored in pixels.
                if (!visible && column.ActualWidth > 0) companionWidth = new(column.ActualWidth);
                // A splitter drag rescales both star columns; the terminal's is reset to 1* so it owns the rest.
                columns[0].Width = new GridLength(1, GridUnitType.Star);
                column.Width = visible ? companionWidth : new GridLength(0);
            }
            companion.Content = value;
            companion.IsVisible = companionSplitter.IsVisible = visible;
        }
    }

    /// <summary>Types into the shell as if typed; ignored until the shell has started.</summary>
    public void Write(string data)
    {
        if (Backend is { } backend && backend.Started.IsCompletedSuccessfully && !IsExited && !IsDetached && !IsWatching) backend.Write(data);
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
    /// <summary>Another client holds the terminal and this view shows it live, read-only.</summary>
    public bool IsWatching => detachedNotice.IsVisible && Backend is { Watching: true };
    /// <summary>Another client holds the terminal and its host cannot show it to this one.</summary>
    public bool IsDetached => detachedNotice.IsVisible && !IsWatching;

    public async ValueTask<bool> IsBusyAsync() =>
        Backend is { } backend && !IsExited && !IsFailed && !IsDetached && backend.Started.IsCompletedSuccessfully && await backend.IsBusyAsync();

    public void FocusTerminal()
    {
        if (detachedNotice.IsVisible) takeBack.Focus();
        else if (IsFailed) retry.Focus();
        else Backend?.FocusTerminal();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    public void Restart() => Start(retrying: IsKeyboardFocusWithin, watch: IsWatching);

    // Starting again attaches afresh. Only the notice's button takes the session from another client; every
    // other start of a yielding launch leaves it where it is, and watches it there.
    private async void Start(bool retrying, bool takeOver = false, bool watch = false)
    {
        if (disposed) return;
        var current = ++generation;
        releaseHint?.Invoke(); releaseHint = null; resumeHint.IsVisible = false;
        Backend?.Dispose(); Backend = null; body.Content = null;
        retry.IsEnabled = false; takeBack.IsEnabled = false;
        exitNotice.IsVisible = false;
        if (takeOver) displaced = false;
        ITerminalBackend? backend = null;
        try
        {
            backend = factory(takeOver ? launch with { Yield = false } : watch ? launch with { Yield = true, Watch = true } : launch);
            Backend = backend;
            Action? updateHint = null;
            if (backend is ITerminalResumeHint source)
            {
                void Update() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (current == generation && ReferenceEquals(Backend, backend))
                        resumeHint.IsVisible = !source.NativeResumeHint && source.ResumeHint.Visible && backend.Started.IsCompletedSuccessfully &&
                            !backend.Watching && !IsExited && !IsDetached && !IsFailed;
                });
                source.ResumeHint.Changed += Update;
                releaseHint = () => source.ResumeHint.Changed -= Update;
                updateHint = Update;
            }
            body.Content = backend.View;
            failure.IsVisible = false;
            detachedNotice.IsVisible = false; body.IsVisible = true;
            await backend.Started;
            if (current != generation) return;
            updateHint?.Invoke();
            backend.SetAgentNewline(agentNewline);
            if (backend.Watching)
            {
                Offer(displaced ? "Read-only: another client took this terminal over." : "Read-only: in use by another client.");
                if (retrying) takeBack.Focus();
            }
            else if (retrying) backend.FocusTerminal();
            await Task.WhenAny(backend.Exited, backend.Detached);
            if (current != generation) return;
            resumeHint.IsVisible = false;
            if (backend.Detached.IsCompleted)
            {
                // Taken over, or held elsewhere when a renderer that cannot watch attached: watch it from now on,
                // without the keyboard, so what was being typed here cannot take it straight back.
                if (!watch || !backend.Yielded)
                {
                    displaced |= !backend.Yielded;
                    Start(retrying: false, watch: true);
                    return;
                }
                // A host that predates watching returned a watch detached; hide the surface rather than show a stale screen as live.
                body.IsVisible = false;
                Offer(displaced ? "This terminal is open somewhere else." : "This terminal is in use by another client.");
                return;
            }
            var code = await backend.Exited;
            detachedNotice.IsVisible = false;
            exitNotice.Text = $"[process exited with code {code}]";
            exitNotice.IsVisible = true;
            if (backend is SkiaTerminal { KeyBar: { } keys }) keys.IsVisible = false;
        }
        catch (Exception error)
        {
            if (current != generation) return;
            resumeHint.IsVisible = false;
            backend?.Dispose();
            Backend = null; body.Content = null;
            body.IsVisible = true; detachedNotice.IsVisible = false;
            failureText.Text = error.Message;
            failure.IsVisible = true;
            retry.IsEnabled = true;
            if (retrying) retry.Focus();
        }
    }

    private void Offer(string text)
    {
        detachedText.Text = text;
        takeBack.Content = displaced ? "Take it back" : "Take over";
        detachedNotice.IsVisible = true;
        takeBack.IsEnabled = true;
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
        releaseHint?.Invoke(); releaseHint = null; resumeHint.IsVisible = false;
        Backend?.Dispose(); Backend = null;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (Backend is ITerminalResumeHint source) source.ResumeHint.Dismiss();
        resumeHint.IsVisible = false;
        base.OnDetachedFromVisualTree(e);
    }
}