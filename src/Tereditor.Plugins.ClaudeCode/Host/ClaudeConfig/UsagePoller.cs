namespace Tereditor.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// Keeps the account usage fresh without anyone asking: <c>claude -p /usage</c> starts no model turn and spends no tokens, so
/// the host reads it at start and then every <see cref="Interval"/>. One read runs at a time; a tick that finds one running does
/// nothing. A failed read doubles the wait up to <see cref="MaxWait"/> and a good one resets it. A manual refresh runs at once
/// and restarts the wait. A read that cannot run (no CLI, not signed in) is skipped quietly. Time comes from an injected clock
/// and <see cref="Tick"/> is driven by a timer, so a check drives it without waiting.
/// </summary>
internal sealed class ClaudeUsagePoller(Func<DateTimeOffset> now, Func<bool, Task<UsagePoll>> read, Action<bool> changed) : IDisposable
{
    /// <summary>The wait between background reads.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>The longest wait after failures.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    /// <summary>How old the numbers may be when the Account tab opens before it asks for a read.</summary>
    public static readonly TimeSpan OpenedAge = TimeSpan.FromMinutes(1);

    /// <summary>How often the timer calls <see cref="Tick"/>.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task<UsagePoll>? running;
    private DateTimeOffset due = DateTimeOffset.MinValue;
    private DateTimeOffset? lastGood, lastAttempt;
    private int failures;

    /// <summary>The next background read is due at this time.</summary>
    public DateTimeOffset Due { get { lock (gate) return due; } }

    /// <summary>Failed reads in a row.</summary>
    public int Failures { get { lock (gate) return failures; } }

    /// <summary>Reads once now in the background, then ticks every <see cref="TickEvery"/> until disposed.</summary>
    public void Start()
    {
        Tick();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TickEvery);
            try { while (await timer.WaitForNextTickAsync(stop.Token)) Tick(); }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Starts a read if one is due and none is running; returns whether it did.</summary>
    public bool Tick()
    {
        lock (gate)
        {
            if (running is not null || now() < due) return false;
            Begin(manual: false);
            return true;
        }
    }

    /// <summary>The Account tab opened: reads in the background when the numbers and the last attempt are both over a minute old.</summary>
    public bool Opened()
    {
        lock (gate)
        {
            var at = now();
            if (running is not null || lastGood is { } good && at - good < OpenedAge || lastAttempt is { } attempt && at - attempt < OpenedAge) return false;
            Begin(manual: false);
            return true;
        }
    }

    /// <summary>The refresh button: reads now (joining a read that is running) and restarts the wait.</summary>
    public Task<UsagePoll> RefreshAsync()
    {
        lock (gate) return running ?? Begin(manual: true);
    }

    private Task<UsagePoll> Begin(bool manual)
    {
        var task = Task.Run(async () =>
        {
            UsagePoll result;
            try { result = await read(manual); }
            catch (Exception error) when (error is not OperationCanceledException) { result = UsagePoll.Failed; }
            lock (gate)
            {
                var at = now();
                lastAttempt = at;
                if (result == UsagePoll.Ok) { failures = 0; lastGood = at; }
                else if (result == UsagePoll.Failed) failures++;
                var wait = result == UsagePoll.Failed ? TimeSpan.FromTicks(Math.Min(MaxWait.Ticks, Interval.Ticks << Math.Min(failures, 8))) : Interval;
                due = at + wait;
                running = null;
            }
            if (result != UsagePoll.Skipped) changed(result == UsagePoll.Failed);
            return result;
        });
        running = task;
        return task;
    }

    public void Dispose() => stop.Cancel();
}