using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;
using Tereditor.Plugins.UI.Kit.Visualization;

namespace Tereditor.UI.Terminal;

// A terminal tab's body: attaches to its host session, explains start failures with an in-place retry, shows a
// terminal another client holds live and read-only with an offer to take it over, or back, and says when the
// shell has exited.
public sealed partial class TerminalView : UserControl, IDisposable
{
    private readonly TerminalFactory factory;
    private TerminalLaunch launch;
    private readonly ContentControl body;
    private readonly Control failure;
    private readonly SelectableTextBlock failureText;
    private readonly Button retry;
    private readonly Control detachedNotice;
    private readonly TextBlock detachedText;
    private readonly Button takeBack;
    private readonly TextBlock exitNotice;
    private readonly Control resumeHint;
    private readonly Control inputHeld;
    private readonly Border secureIndicator;
    private readonly Avalonia.Threading.DispatcherTimer passwordPoll;
    private Window? window;
    private bool passwordInput, passwordQuery;
    private int inputHolds;
    private int holdEpoch;
    private Action? releaseHint;
    private readonly ContentControl companion;
    private readonly GridSplitter companionSplitter;
    private readonly GridSplitter stackSplitter;
    private readonly Border accessoriesDivider;
    // 45% of the view beside a 1* terminal column.
    private GridLength companionWidth = new(45.0 / 55, GridUnitType.Star);
    private int generation;
    private bool disposed;
    private bool agentNewline;
    // This view held the terminal until another client took it.
    private bool displaced;
    // Font size offsets by session for this process, as Ghostty keeps one per surface: a view rebuilt for the same
    // terminal (a tab switch, a re-attach) keeps its size, and nothing is written to the profile.
    private static readonly Dictionary<string, double> fontOffsets = [];
    private static WeakReference<TerminalView>? lastFocused;
    private double magnified, wheeled;
    private double? pinchStart;

    // Compiled XAML never uses the runtime loader; this public constructor only makes Avalonia register the view (AVLN3001).
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Obsolete("Construct TerminalView with its arguments.", true)]
    public TerminalView() => throw new System.NotSupportedException("TerminalView needs its constructor arguments.");

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
        inputHeld = this.FindControl<Control>("TerminalInputHeld")!;
        secureIndicator = this.FindControl<Border>("TerminalSecureInput")!;
        secureIndicator.Child = Ui.Icon("lock", Ui.Accent, 14);
        passwordPoll = new() { Interval = PasswordPollInterval };
        passwordPoll.Tick += (_, _) => PollPassword();
        AddHandler(GotFocusEvent, (_, _) => UpdateSecureInput(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, _) => UpdateSecureInput(), RoutingStrategies.Bubble, handledEventsToo: true);
        Accessories = this.FindControl<StackPanel>("TerminalAccessories")!;
        companion = this.FindControl<ContentControl>("TerminalCompanion")!;
        companionSplitter = this.FindControl<GridSplitter>("TerminalCompanionSplitter")!;
        stackSplitter = this.FindControl<GridSplitter>("TerminalCompanionStackSplitter")!;
        companionSplitter.DragCompleted += (_, _) => CompanionWidthChanged?.Invoke(CompanionColumns[2].ActualWidth);
        accessoriesDivider = this.FindControl<Border>("TerminalAccessoriesDivider")!;
        this.FindControl<StackPanel>("TerminalAccessories")!.SizeChanged += (_, _) => LayoutCompanion();
        SizeChanged += (_, _) => LayoutCompanion();
        retry.Click += (_, _) => Start(retrying: true);
        this.FindControl<Button>("TerminalResumeOfferResume")!.Click += (_, _) => resumeOffered?.Invoke();
        this.FindControl<Button>("TerminalResumeOfferFresh")!.Click += (_, _) => freshOffered?.Invoke();
        this.FindControl<Button>("TerminalResumeOfferQuit")!.Click += (_, _) => quitOffered?.Invoke();
        var dismiss = Ui.IconButton("close", "Dismiss", () => { dismissedOffer = shownOffer; this.FindControl<Control>("TerminalResumeOffer")!.IsVisible = false; });
        dismiss.Name = "TerminalResumeOfferDismissButton";
        this.FindControl<ContentControl>("TerminalResumeOfferDismiss")!.Content = dismiss;
        takeBack.Click += (_, _) => Start(retrying: true, takeOver: true);
        Focusable = true;
        // Held input never reaches a renderer: keys, composed text and the pointer's paste and menu buttons are
        // swallowed on the way down, so a driven command cannot be interleaved with the user's typing.
        AddHandler(KeyDownEvent, DropWhileHeld, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, DropWhileHeld, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, DropWhileHeld, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (InputHeld && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Zoom gestures over the surface size this terminal's font, not the app: they are claimed here before the
        // window's pinch handler sees them, and the wheel before the renderer scrolls with it.
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            if (!OverSurface(e.Source)) return;
            Magnify(e.Delta.X); e.Handled = true;
        });
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!ZoomGesture.IsZoom(e.KeyModifiers) || !OverSurface(e.Source)) return;
            WheelZoom(e.Delta.Y); e.Handled = true;
        }, RoutingStrategies.Tunnel);
        if (OperatingSystem.IsAndroid())
        {
            GestureRecognizers.Add(new PinchGestureRecognizer());
            AddHandler(PinchEvent, (_, e) =>
            {
                pinchStart ??= FontSizeOffset;
                SetFontSizeOffset(pinchStart.Value + Math.Round((e.Scale - 1) * PointsPerMagnification));
                e.Handled = true;
            });
            AddHandler(PinchEndedEvent, (_, _) => pinchStart = null);
        }
        // A new terminal, not one restored or watched, starts at the size of the terminal focused last, as Ghostty's
        // window-inherit-font-size does.
        if (!launch.Yield && !fontOffsets.ContainsKey(launch.SessionId) && lastFocused?.TryGetTarget(out var from) == true &&
            !from.disposed && from.FontSizeOffset != 0)
            fontOffsets[launch.SessionId] = from.FontSizeOffset;
        Start(retrying: false);
    }

    /// <summary>The bounds of a terminal's own font size offset, in points.</summary>
    public const double MinFontSizeOffset = -8, MaxFontSizeOffset = 48;
    /// <summary>A pinch's magnification per point of font size: a tenth of the gesture changes the size by one point.</summary>
    internal const double PointsPerMagnification = 10;

    /// <summary>Points this terminal's font is above (or below) the renderer's base size, local to this client.</summary>
    public double FontSizeOffset => fontOffsets.GetValueOrDefault(launch.SessionId);

    /// <summary>Ghostty's <c>increase_font_size</c>/<c>decrease_font_size</c>: steps this terminal's font by whole points.</summary>
    public void ChangeFontSize(double points) => SetFontSizeOffset(FontSizeOffset + points);

    /// <summary>Ghostty's <c>reset_font_size</c>: back to the base size.</summary>
    public void ResetFontSize() => SetFontSizeOffset(0);

    private void SetFontSizeOffset(double points)
    {
        points = Math.Clamp(points, MinFontSizeOffset, MaxFontSizeOffset);
        if (points == FontSizeOffset) return;
        if (points == 0) fontOffsets.Remove(launch.SessionId); else fontOffsets[launch.SessionId] = points;
        Backend?.SetFontSizeOffset(points);
    }

    /// <summary>One step of a trackpad pinch: magnification accumulates and the font moves a point per tenth.</summary>
    public void Magnify(double delta)
    {
        if (!double.IsFinite(delta)) return;
        magnified += delta * PointsPerMagnification;
        var points = Math.Truncate(magnified);
        if (points == 0) return;
        magnified -= points;
        ChangeFontSize(points);
    }

    /// <summary>A zooming wheel: a point per notch, a trackpad's fractions carried to the next event.</summary>
    public void WheelZoom(double delta)
    {
        if (!double.IsFinite(delta)) return;
        wheeled += delta;
        var points = Math.Truncate(wheeled);
        if (points == 0) return;
        wheeled -= points;
        ChangeFontSize(points);
    }

    private bool OverSurface(object? source) => source is Visual visual && (visual == body || body.IsVisualAncestorOf(visual));

    private IEnumerable<Control> MenuExtras()
    {
        var reset = new MenuItem { Header = "Reset Font Size", IsEnabled = FontSizeOffset != 0 };
        reset.Click += (_, _) => ResetFontSize();
        return [new Separator(), reset];
    }

    /// <summary>The longest one hold lasts, so a driver that never returns cannot lock the terminal for good.</summary>
    internal static TimeSpan MaxInputHold = TimeSpan.FromSeconds(30);

    /// <summary>True while a driven entry holds the user's keyboard out of the terminal.</summary>
    public bool InputHeld => Volatile.Read(ref inputHolds) > 0;

    private void DropWhileHeld(object? sender, RoutedEventArgs e)
    {
        if (InputHeld) e.Handled = true;
    }

    /// <summary>
    /// Holds the user's keys, text and paste out of the terminal until the returned handle is disposed, so a plugin typing
    /// a command is not interleaved with them; what the user types meanwhile is dropped, not queued. Programmatic writes
    /// are unaffected. Holds nest. Each ends by itself after <see cref="MaxInputHold"/>, when the terminal restarts or
    /// its shell exits, and when the view is disposed. Callable from any thread.
    /// </summary>
    public IDisposable HoldInput() => new InputHold(this);

    private void ReleaseAllHolds()
    {
        Interlocked.Increment(ref holdEpoch);
        Interlocked.Exchange(ref inputHolds, 0);
        ShowInputHeld();
    }

    private void ShowInputHeld() => Avalonia.Threading.Dispatcher.UIThread.Post(() => inputHeld.IsVisible = InputHeld && !disposed);

    private sealed class InputHold : IDisposable
    {
        private readonly TerminalView view;
        private readonly CancellationTokenSource expiry = new();
        private int released;
        private readonly int epoch;

        public InputHold(TerminalView view)
        {
            this.view = view;
            epoch = Volatile.Read(ref view.holdEpoch);
            Interlocked.Increment(ref view.inputHolds);
            view.ShowInputHeld();
            _ = Task.Delay(MaxInputHold, expiry.Token).ContinueWith(_ => Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            expiry.Cancel();
            expiry.Dispose();
            // A reset already zeroed the count this hold added to.
            if (epoch == Volatile.Read(ref view.holdEpoch)) Interlocked.Decrement(ref view.inputHolds);
            view.ShowInputHeld();
        }
    }

    /// <summary>How often a focused terminal asks its host whether a password is being read; Ghostty polls termios every 200 ms.</summary>
    internal static TimeSpan PasswordPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>True while this terminal is the reason secure keyboard entry is on; its lock shows.</summary>
    public bool SecureInputShown => secureIndicator.IsVisible;

    // Only a focused terminal in the active window, live and held by this client, asks its host about password prompts.
    private bool SecureInputEligible => !disposed && SecureInput.Shared is { Supported: true, Automatic: true } && IsKeyboardFocusWithin &&
        window is { IsActive: true } && Backend is { } backend && backend.Started.IsCompletedSuccessfully && !IsExited && !IsDetached && !IsWatching && !IsFailed;

    /// <summary>Re-evaluates this terminal's claim on secure input after focus, activation, lifecycle or setting changes.</summary>
    internal void UpdateSecureInput()
    {
        var eligible = SecureInputEligible;
        if (!eligible) passwordInput = false;
        if (eligible && !passwordPoll.IsEnabled) { passwordPoll.Start(); PollPassword(); }
        else if (!eligible) passwordPoll.Stop();
        if (disposed) SecureInput.Shared.Remove(this);
        else SecureInput.Shared.SetScoped(this, eligible && passwordInput);
        ShowSecureInput();
    }

    // The host answers from the PTY's termios, off the UI thread; a stale answer for a replaced backend is dropped.
    private async void PollPassword()
    {
        if (passwordQuery || Backend is not { } backend) return;
        passwordQuery = true;
        bool reading;
        try { reading = await Task.Run(async () => await backend.IsPasswordInputAsync()); }
        catch (Exception) { reading = false; }
        finally { passwordQuery = false; }
        if (!ReferenceEquals(Backend, backend)) return;
        passwordInput = reading;
        UpdateSecureInput();
    }

    private void ShowSecureInput() => secureIndicator.IsVisible = !disposed && IsKeyboardFocusWithin && SecureInput.Shared.Protects(this);

    private void SecureInputChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(ShowSecureInput);

    private void WindowActivationChanged(object? sender, EventArgs e) => UpdateSecureInput();

    public const string SecureInputMenuHeader = "Secure Keyboard Entry";

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null) { window.Activated += WindowActivationChanged; window.Deactivated += WindowActivationChanged; }
        SecureInput.Shared.Changed += SecureInputChanged;
        UpdateSecureInput();
    }

    internal bool FileDropWired { get; set; }
    /// <summary>Whether this terminal's agent can take attachments now; set by the workbench's file-drop wiring.</summary>
    internal Func<bool>? AcceptsAttachments { get; set; }
    /// <summary>Uploads external bytes to the host and inserts their paths into the agent's prompt; false if nothing was attached.</summary>
    internal Func<IReadOnlyList<Tereditor.Host.Abstractions.FileTransferEntry>, Task<bool>>? AttachEntries { get; set; }
    internal bool FileDropActive
    {
        set => this.FindControl<Border>("FileDropHighlight")!.IsVisible = value;
    }

    public ITerminalBackend? Backend { get; private set; }
    /// <summary>Raised on the UI thread when the shell printed something, where the renderer tells.</summary>
    internal event Action? Output;
    public string SessionId => launch.SessionId;
    public TerminalLaunch Launch => launch;
    /// <summary>Plugin accessory rows shown below the surface.</summary>
    public StackPanel Accessories { get; }

    private Action? resumeOffered, freshOffered, quitOffered;
    private string? shownOffer, dismissedOffer;

    /// <summary>
    /// Shows the host's resume offer for this tab above the surface, or hides it when <paramref name="text"/> is null. A null
    /// <paramref name="resume"/> leaves only Start fresh, for a session open elsewhere. Both buttons take keyboard focus and
    /// carry access keys (Alt+R, Alt+F). A <paramref name="quit"/> adds "Quit that process and resume" (Alt+Q), offered only for a stray
    /// owner the host may end. A session open elsewhere (no resume) is informational and gets a close cross: the dismissal holds for
    /// this view until <paramref name="key"/> (the session and its owner) changes, and answers nothing.
    /// </summary>
    internal void ShowResumeOffer(string? text, Action? resume, Action? fresh, Action? quit = null, string? key = null)
    {
        var offer = this.FindControl<Control>("TerminalResumeOffer")!;
        resumeOffered = resume; freshOffered = fresh; quitOffered = quit;
        shownOffer = text is null ? null : key;
        if (text is null) dismissedOffer = null;
        this.FindControl<Button>("TerminalResumeOfferQuit")!.IsVisible = text is not null && quit is not null;
        this.FindControl<Control>("TerminalResumeOfferDismiss")!.IsVisible = text is not null && resume is null && key is not null;
        offer.IsVisible = text is not null && (key is null || key != dismissedOffer);
        if (text is null) return;
        var offerText = this.FindControl<TextBlock>("TerminalResumeOfferText")!;
        offerText.Text = text;
        ToolTip.SetTip(offerText, text);
        var resumeButton = this.FindControl<Button>("TerminalResumeOfferResume")!;
        resumeButton.IsVisible = resume is not null;
        this.FindControl<Button>("TerminalResumeOfferFresh")!.IsVisible = fresh is not null;
    }

    /// <summary>Whether the resume offer is shown, and the text and buttons it shows.</summary>
    internal (bool Visible, string? Text, bool Resume, bool Fresh, bool Quit) ResumeOffer =>
        (this.FindControl<Control>("TerminalResumeOffer")!.IsVisible, this.FindControl<TextBlock>("TerminalResumeOfferText")!.Text,
            this.FindControl<Button>("TerminalResumeOfferResume")!.IsVisible, this.FindControl<Button>("TerminalResumeOfferFresh")!.IsVisible,
            this.FindControl<Button>("TerminalResumeOfferQuit")!.IsVisible);

    /// <summary>Presses the offer's Resume (true) or Start fresh (false) button, as a click would.</summary>
    internal void AnswerResumeOffer(bool resume) => (resume ? resumeOffered : freshOffered)?.Invoke();

    /// <summary>Presses the offer's "Quit that process and resume" button, as a click would; false when it is not shown.</summary>
    internal bool QuitResumeOffer() { if (quitOffered is not { } quit) return false; quit(); return true; }

    /// <summary>Below this view width the companion stacks below the surface instead of squeezing it.</summary>
    public const double CompanionStackBelow = 560;

    /// <summary>A stacked companion never takes more than this share of the height; the surface keeps the rest.</summary>
    public const double CompanionStackMaxShare = 0.6;

    /// <summary>The least height a stacked surface keeps (several rows plus the composer) and the least a stacked companion keeps.</summary>
    public const double StackedSurfaceMinHeight = 200, StackedCompanionMinHeight = 80;

    /// <summary>Whether the open companion currently is stacked below the surface (the view is narrower than <see cref="CompanionStackBelow"/>).</summary>
    public bool CompanionStacked { get; private set; }

    /// <summary>Raised with the column's width in pixels after the user drags the splitter or closes a resized companion.</summary>
    public event Action<double>? CompanionWidthChanged;

    /// <summary>Where the rows that stand for the tab's companions sit, above the surface.</summary>
    public StackPanel CompanionStrips => this.FindControl<StackPanel>("TerminalCompanionStrips")!;

    private ColumnDefinitions CompanionColumns => ((Grid)companion.Parent!).ColumnDefinitions;

    /// <summary>
    /// The companion column's width in pixels when it is open or was resized, else null (the first opening takes 45%).
    /// Setting it restores a saved width; it applies at once when the companion is open beside the surface.
    /// </summary>
    public double? CompanionWidth
    {
        get => companion.IsVisible && !CompanionStacked && CompanionColumns[2].ActualWidth > 0 ? CompanionColumns[2].ActualWidth
            : companionWidth.IsAbsolute ? companionWidth.Value : null;
        set
        {
            if (value is not > 0) return;
            companionWidth = new GridLength(value.Value);
            if (!companion.IsVisible || CompanionStacked) return;
            CompanionColumns[0].Width = new GridLength(1, GridUnitType.Star);
            CompanionColumns[2].Width = companionWidth;
        }
    }

    /// <summary>The embedded companion pane beside the surface (below it when narrow); null closes it.</summary>
    public Control? Companion
    {
        get => companion.Content as Control;
        set
        {
            var visible = value is not null;
            if (visible != companion.IsVisible)
            {
                var columns = CompanionColumns;
                var column = columns[2];
                // The first opening takes 45%; afterwards the width the user left is restored in pixels.
                if (!visible && !CompanionStacked && column.ActualWidth > 0)
                {
                    companionWidth = new(column.ActualWidth);
                    CompanionWidthChanged?.Invoke(column.ActualWidth);
                }
                // A splitter drag rescales both star columns; the terminal's is reset to 1* so it owns the rest.
                columns[0].Width = new GridLength(1, GridUnitType.Star);
                column.Width = visible && !NarrowForCompanion ? companionWidth : new GridLength(0);
            }
            companion.Content = value;
            companion.IsVisible = visible;
            LayoutCompanion();
        }
    }

    private bool NarrowForCompanion => Bounds.Width is > 0 && Bounds.Width < CompanionStackBelow;

    // Narrow, the open companion spans the view below the surface, split by a draggable splitter; wide, it is the third column again.
    private void LayoutCompanion()
    {
        var stacked = companion.IsVisible && NarrowForCompanion;
        var rows = ((Grid)companion.Parent!).RowDefinitions;
        if (stacked != CompanionStacked)
        {
            CompanionStacked = stacked;
            var columns = CompanionColumns;
            Grid.SetRow(companion, stacked ? 3 : 1);
            Grid.SetColumn(companion, stacked ? 0 : 2);
            Grid.SetColumnSpan(companion, stacked ? 3 : 1);
            rows[1].Height = new GridLength(stacked ? 3 : 1, GridUnitType.Star);
            rows[3].Height = stacked ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            if (stacked)
            {
                if (columns[2].ActualWidth > 0) companionWidth = new(columns[2].ActualWidth);
                columns[2].Width = new GridLength(0);
            }
            else
            {
                rows[1].MinHeight = rows[3].MinHeight = 0;
                if (companion.IsVisible)
                {
                    columns[0].Width = new GridLength(1, GridUnitType.Star);
                    columns[2].Width = companionWidth;
                }
            }
        }
        if (stacked && Bounds.Height > 0)
        {
            rows[1].MinHeight = Math.Max(StackedSurfaceMinHeight, Bounds.Height * (1 - CompanionStackMaxShare));
            rows[3].MinHeight = StackedCompanionMinHeight;
        }
        companionSplitter.IsVisible = companion.IsVisible && !stacked;
        stackSplitter.IsVisible = stacked;
        accessoriesDivider.IsVisible = companion.IsVisible && this.FindControl<StackPanel>("TerminalAccessories")!.Bounds.Height > 0;
    }

    /// <summary>Types into the shell as if typed; ignored until the shell has started.</summary>
    public void Write(string data)
    {
        if (Backend is { } backend && backend.Started.IsCompletedSuccessfully && !IsExited && !IsDetached && !IsWatching) backend.Write(data);
    }

    /// <summary>The surface's screen and scrollback as text, oldest line first.</summary>
    public string ReadScreen() => Backend is { } backend && backend.Started.IsCompletedSuccessfully ? backend.ReadScreen() : "";

    /// <summary>The screen as <see cref="ReadScreen()"/> with faint cells blanked where the renderer knows them; see <see cref="ITerminalBackend.ReadScreen(bool)"/>.</summary>
    public string ReadScreen(bool omitFaint) => Backend is { } backend && backend.Started.IsCompletedSuccessfully ? backend.ReadScreen(omitFaint) : "";
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

    /// <summary>What the host read from the shell's integration; null without a running backend or a report.</summary>
    public async ValueTask<Tereditor.Host.Abstractions.TerminalShellStatus?> ShellStatusAsync() =>
        Backend is { } backend && !IsFailed && backend.Started.IsCompletedSuccessfully ? await backend.ShellStatusAsync() : null;

    public async ValueTask<bool> IsBusyAsync() =>
        Backend is { } backend && !IsExited && !IsFailed && !IsDetached && backend.Started.IsCompletedSuccessfully && await backend.IsBusyAsync();

    public void FocusTerminal()
    {
        if (detachedNotice.IsVisible) takeBack.Focus();
        else if (IsFailed) retry.Focus();
        else
        {
            Backend?.FocusTerminal();
            // A new terminal is focused before its renderer has started (New terminal focuses the pane at once); keep
            // the request so the surface takes the keyboard when it starts instead of leaving focus on the tab.
            focusOnStart = Backend is not { Started.IsCompletedSuccessfully: true };
        }
    }

    private bool focusOnStart;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        lastFocused = new(this);
        if (e.Source == this) FocusTerminal();
    }

    public void Restart() => Start(retrying: IsKeyboardFocusWithin, watch: IsWatching);

    // Starting again attaches afresh. Only the notice's button takes the session from another client; every
    // other start of a yielding launch leaves it where it is, and watches it there.
    private async void Start(bool retrying, bool takeOver = false, bool watch = false)
    {
        if (disposed) return;
        var current = ++generation;
        ReleaseAllHolds();
        releaseHint?.Invoke(); releaseHint = null; resumeHint.IsVisible = false;
        Backend?.Dispose(); Backend = null; body.Content = null;
        UpdateSecureInput();
        retry.IsEnabled = false; takeBack.IsEnabled = false;
        exitNotice.IsVisible = false;
        if (takeOver) displaced = false;
        ITerminalBackend? backend = null;
        try
        {
            backend = factory(takeOver ? launch with { Yield = false } : watch ? launch with { Yield = true, Watch = true } : launch);
            Backend = backend;
            backend.SetFontSizeOffset(FontSizeOffset);
            backend.SetMenuExtras(MenuExtras);
            if (backend is ITerminalOutput printing) printing.Output += () => { if (ReferenceEquals(Backend, backend)) Output?.Invoke(); };
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
            // The program ran once, in the session it started; starting this tab again opens a plain shell.
            launch = launch with { Program = null };
            if (current != generation) return;
            updateHint?.Invoke();
            backend.SetAgentNewline(agentNewline);
            if (backend.Watching)
            {
                Offer(displaced ? "Read-only: another client took this terminal over." : "Read-only: in use by another client.");
                if (retrying) takeBack.Focus();
            }
            else if (retrying || focusOnStart) backend.FocusTerminal();
            focusOnStart = false;
            UpdateSecureInput();
            await Task.WhenAny(backend.Exited, backend.Detached);
            if (current != generation) return;
            UpdateSecureInput();
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
            ReleaseAllHolds();
            detachedNotice.IsVisible = false;
            exitNotice.Text = $"[process exited with code {code}]";
            exitNotice.IsVisible = true;
            UpdateSecureInput();
            if (backend is SkiaTerminal { KeyBar: { } keys }) keys.Retire();
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
            UpdateSecureInput();
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
        ReleaseAllHolds();
        releaseHint?.Invoke(); releaseHint = null; resumeHint.IsVisible = false;
        Backend?.Dispose(); Backend = null;
        UpdateSecureInput();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (Backend is ITerminalResumeHint source) source.ResumeHint.Dismiss();
        resumeHint.IsVisible = false;
        if (window is not null) { window.Activated -= WindowActivationChanged; window.Deactivated -= WindowActivationChanged; window = null; }
        SecureInput.Shared.Changed -= SecureInputChanged;
        passwordPoll.Stop(); passwordInput = false;
        SecureInput.Shared.Remove(this);
        ShowSecureInput();
        base.OnDetachedFromVisualTree(e);
    }
}