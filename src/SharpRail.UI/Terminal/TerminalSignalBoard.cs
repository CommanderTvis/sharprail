using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

/// <summary>
/// What every terminal's programs currently show its viewers, by host session: an OSC 9;4 progress and an unseen bell.
/// Lives on the UI thread; tab chrome observes it without being rebuilt.
/// </summary>
internal sealed class TerminalSignalBoard
{
    private readonly Dictionary<string, (TerminalProgressState State, int Percent)> progress = [];
    private readonly HashSet<string> bells = [];

    /// <summary>Raised with the session whose progress or bell changed.</summary>
    public event Action<string>? Changed;

    public (TerminalProgressState State, int Percent) Progress(string session) => progress.GetValueOrDefault(session);
    public bool Bell(string session) => bells.Contains(session);

    public void SetProgress(string session, TerminalProgressState state, int percent)
    {
        if (state == TerminalProgressState.None ? !progress.Remove(session) : progress.GetValueOrDefault(session) == (state, percent)) return;
        if (state != TerminalProgressState.None) progress[session] = (state, percent);
        Changed?.Invoke(session);
    }

    public void RingBell(string session) { if (bells.Add(session)) Changed?.Invoke(session); }
    public void ClearBell(string session) { if (bells.Remove(session)) Changed?.Invoke(session); }

    /// <summary>A host that restarted or reconnected sends every progress again; nothing it no longer reports survives.</summary>
    public void ResetProgress()
    {
        var sessions = progress.Keys.ToArray();
        progress.Clear();
        foreach (var session in sessions) Changed?.Invoke(session);
    }

    /// <summary>A thin bar along the bottom of a tab or chip, coloured by the progress state; null for a tab that is not a terminal.</summary>
    public Control? Strip(string workspace, Docking.DockTab tab) => tab.Kind == "terminal" ? new TerminalProgressStrip(this, TerminalLaunch.SessionFor(workspace, tab.Id)) : null;

    /// <summary>A bell beside the title until the terminal is viewed (Ghostty's bell title feature).</summary>
    public Control? BellMark(string workspace, Docking.DockTab tab) => tab.Kind == "terminal" ? new TerminalBellMark(this, TerminalLaunch.SessionFor(workspace, tab.Id)) : null;
}

internal abstract class SignalObserver : Panel
{
    protected readonly TerminalSignalBoard Board;
    protected readonly string Session;

    protected SignalObserver(TerminalSignalBoard board, string session)
    {
        Board = board; Session = session; IsHitTestVisible = false;
        AttachedToVisualTree += (_, _) => { Board.Changed += Changed; Update(); };
        DetachedFromVisualTree += (_, _) => { Board.Changed -= Changed; Stop(); };
    }

    private void Changed(string session) { if (session == Session) Update(); }
    protected abstract void Update();
    protected virtual void Stop() { }
}

internal sealed class TerminalBellMark : SignalObserver
{
    public TerminalBellMark(TerminalSignalBoard board, string session) : base(board, session)
    {
        Name = "TerminalBellMark";
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 0, 0);
        Children.Add(Ui.Icon("bell", Ui.Warning, 12));
        ToolTip.SetTip(this, "Bell");
        AutomationProperties.SetName(this, "Bell");
        IsVisible = false;
    }

    protected override void Update() => IsVisible = Board.Bell(Session);
}

/// <summary>
/// Ghostty's surface progress bar: a determinate fill for a percent, a bouncing quarter-width segment over a faint track
/// for a report without one, and a full bar for a paused report without a percent. Normal and indeterminate use the
/// accent, error the danger and paused the warning theme role.
/// </summary>
internal sealed class TerminalProgressStrip : SignalObserver
{
    private const double SegmentRatio = 0.25;
    private static readonly TimeSpan BounceTime = TimeSpan.FromSeconds(1.2);
    private readonly Border track = new();
    private readonly Border fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private DispatcherTimer? animation;
    private DateTime started;

    public TerminalProgressStrip(TerminalSignalBoard board, string session) : base(board, session)
    {
        Name = "TerminalProgress";
        Height = 2;
        VerticalAlignment = VerticalAlignment.Bottom;
        ClipToBounds = true;
        Children.Add(track); Children.Add(fill);
        IsVisible = false;
        SizeChanged += (_, _) => Layout();
    }

    /// <summary>The state drawn, for checks.</summary>
    internal TerminalProgressState State { get; private set; }
    internal int Percent { get; private set; } = -1;
    internal bool Animating => animation?.IsEnabled == true;

    protected override void Update()
    {
        (State, Percent) = Board.Progress(Session);
        IsVisible = State != TerminalProgressState.None;
        var brush = State switch { TerminalProgressState.Error => Ui.Danger, TerminalProgressState.Paused => Ui.Warning, _ => Ui.Accent };
        fill.Background = brush;
        track.Background = State switch { TerminalProgressState.Error => Ui.DangerWash, TerminalProgressState.Paused => Ui.WarningWash, _ => Ui.PrimaryMuted };
        AutomationProperties.SetName(this, State switch
        {
            TerminalProgressState.Error => "Terminal progress - Error",
            TerminalProgressState.Paused => "Terminal progress - Paused",
            TerminalProgressState.Indeterminate => "Terminal progress - In progress",
            _ => "Terminal progress"
        });
        if (Bouncing) Start(); else Stop();
        Layout();
    }

    // A percent wins; paused without one reads as complete; any other report without one bounces.
    private bool Bouncing => State != TerminalProgressState.None && Percent < 0 && State != TerminalProgressState.Paused;

    private void Layout()
    {
        var width = Bounds.Width;
        track.IsVisible = Bouncing;
        if (Bouncing)
        {
            var phase = (DateTime.UtcNow - started).TotalSeconds / BounceTime.TotalSeconds % 2;
            var position = phase <= 1 ? phase : 2 - phase;
            position = position * position * (3 - 2 * position);
            fill.Width = width * SegmentRatio;
            fill.Margin = new Thickness(position * width * (1 - SegmentRatio), 0, 0, 0);
        }
        else
        {
            fill.Width = width * (Percent < 0 ? 100 : Percent) / 100;
            fill.Margin = default;
        }
    }

    private void Start()
    {
        if (animation is { IsEnabled: true }) return;
        started = DateTime.UtcNow;
        animation ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Layout());
        animation.Start();
    }

    protected override void Stop() => animation?.Stop();
}