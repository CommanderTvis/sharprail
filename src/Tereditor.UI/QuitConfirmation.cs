namespace SharpRail.UI;

public enum QuitHint { Hidden, Armed, Release, Quitting }

/// <summary>
/// The confirmed-quit gesture: a hold of <see cref="HoldMs"/> or a second press within
/// <see cref="DoublePressMs"/> arms a quit that completes on key release. A lone tap expires silently.
/// Pure rule with injected input, clock and scheduler; it knows nothing of windows or keys.
/// </summary>
public sealed class QuitConfirmation(
    Func<bool?> readHeld, Func<bool> canShowHint, Action quit, Action<QuitHint> onHint,
    Func<long> now, Func<Action, TimeSpan, IDisposable> every)
{
    public const long HoldMs = 1200, DoublePressMs = 500, PollMs = 40;

    private enum Phase { Idle, Holding, Release, Waiting, Quitting }

    private Phase phase = Phase.Idle;
    private long firstPressAt, windowStart;
    private IDisposable? polling;

    public void Press(bool held)
    {
        if (phase is Phase.Quitting or Phase.Holding or Phase.Release) return;
        if (phase == Phase.Idle) { Arm(held); return; }
        if (now() - windowStart > DoublePressMs) Arm(held);
        else if (held) AwaitRelease();
        else QuitNow();
    }

    public void Sync()
    {
        if (phase is Phase.Idle or Phase.Quitting) return;
        if (readHeld() is not { } held) { Cancel(); return; }
        var time = now();
        if (phase == Phase.Holding)
        {
            if (held && time - firstPressAt >= HoldMs) { phase = Phase.Release; onHint(QuitHint.Release); return; }
            if (!held) { phase = Phase.Waiting; windowStart = time; }
        }
        if (phase == Phase.Release && !held) { QuitNow(); return; }
        if (phase == Phase.Waiting && time - windowStart > DoublePressMs) Reset();
    }

    /// <summary>Focus loss: an unconfirmed gesture ends, one already confirmed completes.</summary>
    public void Cancel()
    {
        if (phase == Phase.Release) QuitNow();
        else if (phase is not (Phase.Idle or Phase.Quitting)) Reset();
    }

    public void QuitNow()
    {
        if (phase == Phase.Quitting) return;
        phase = Phase.Quitting; StopPolling();
        onHint(QuitHint.Quitting);
        quit();
    }

    public void Reset()
    {
        phase = Phase.Idle; StopPolling();
        onHint(QuitHint.Hidden);
    }

    private void StopPolling() { polling?.Dispose(); polling = null; }

    private void Arm(bool held)
    {
        if (!canShowHint())
        {
            if (held) AwaitRelease(); else QuitNow();
            return;
        }
        firstPressAt = windowStart = now();
        phase = held ? Phase.Holding : Phase.Waiting;
        onHint(QuitHint.Armed);
        polling ??= every(Sync, TimeSpan.FromMilliseconds(PollMs));
    }

    private void AwaitRelease()
    {
        phase = Phase.Release;
        onHint(QuitHint.Release);
        polling ??= every(Sync, TimeSpan.FromMilliseconds(PollMs));
    }
}