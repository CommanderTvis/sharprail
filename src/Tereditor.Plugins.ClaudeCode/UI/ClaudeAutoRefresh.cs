namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// When the Claude pane refreshes usage and context on its own: a minute tick asks <see cref="Tick"/>, which starts the same
/// refresh the pane's button does only while the pane is visible, none is running, and the last read is an hour old or missing.
/// The clock and the pane's state are injected so a check can drive it.
/// </summary>
internal sealed class ClaudeAutoRefresh(Func<DateTimeOffset> now, Func<bool> visible, Func<bool> running, Func<DateTimeOffset?> lastRead, Action refresh)
{
    /// <summary>How old the shown usage and context may get before the pane refreshes them by itself.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How often the pane re-renders "Measured … ago" and asks <see cref="Tick"/>.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(1);

    /// <summary>Refreshes when due; returns whether it did.</summary>
    public bool Tick()
    {
        if (!visible() || running() || lastRead() is { } at && now() - at < Interval) return false;
        refresh();
        return true;
    }
}