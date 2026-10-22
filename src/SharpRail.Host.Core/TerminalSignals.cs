using System.Text;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

// Finds what a program asks the terminal's viewers for in its live output: OSC 9;4 progress, OSC 9 and
// OSC 777;notify notifications, BEL outside control strings, and a full reset (RIS), which removes the progress.
// Escape sequences may split across reads, so the scanner keeps its state between chunks.
internal sealed class TerminalSignalParser
{
    private const int MaxOsc = 4096;
    private static readonly TimeSpan BellInterval = TimeSpan.FromMilliseconds(100);
    private enum State { Ground, Escape, Osc, OscEscape, String, StringEscape }

    private readonly List<byte> osc = [];
    // Kitty OSC 99 notifications still receiving chunks (d=0), by identifier.
    private readonly Dictionary<string, (string Title, string Body)> kitty = [];
    private readonly Func<TimeSpan> clock;
    private State state;
    private bool overflow;
    private TimeSpan? lastBell;

    internal TerminalSignalParser(Func<TimeSpan>? clock = null) => this.clock = clock ?? (() => TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond));

    internal void Scan(ReadOnlySpan<byte> data, string session, Action<TerminalSignal> signal)
    {
        foreach (var value in data)
        {
            switch (state)
            {
                case State.Ground:
                    if (value == 0x1b) state = State.Escape;
                    else if (value == 0x07) Bell(session, signal);
                    break;
                case State.Escape:
                    Escape(value, session, signal);
                    break;
                case State.Osc:
                    if (value == 0x07) { Dispatch(session, signal); state = State.Ground; }
                    else if (value == 0x1b) state = State.OscEscape;
                    else if (value is 0x18 or 0x1a) state = State.Ground;
                    else if (osc.Count < MaxOsc) osc.Add(value);
                    else overflow = true;
                    break;
                case State.OscEscape:
                    if (value == (byte)'\\') { Dispatch(session, signal); state = State.Ground; }
                    else Escape(value, session, signal);
                    break;
                case State.String:
                    if (value == 0x1b) state = State.StringEscape;
                    else if (value is 0x18 or 0x1a) state = State.Ground;
                    break;
                case State.StringEscape:
                    state = value == (byte)'\\' ? State.Ground : value == 0x1b ? State.StringEscape : State.String;
                    break;
            }
        }
    }

    private void Escape(byte value, string session, Action<TerminalSignal> signal)
    {
        switch (value)
        {
            case (byte)']': osc.Clear(); overflow = false; state = State.Osc; break;
            case (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_': state = State.String; break;
            case (byte)'c':
                state = State.Ground;
                signal(new(session, TerminalSignalKind.Progress) { Progress = TerminalProgressState.None });
                break;
            case 0x1b: state = State.Escape; break;
            default: state = State.Ground; break;
        }
    }

    private void Bell(string session, Action<TerminalSignal> signal)
    {
        var now = clock();
        if (lastBell is { } last && now - last < BellInterval) return;
        lastBell = now;
        signal(new(session, TerminalSignalKind.Bell));
    }

    private void Dispatch(string session, Action<TerminalSignal> signal)
    {
        if (overflow) return;
        var text = Encoding.UTF8.GetString(osc.ToArray());
        if (text.StartsWith("99;", StringComparison.Ordinal)) { if (Kitty(text[3..], session) is { } notification) signal(notification); return; }
        if (Parse(text, session) is { } parsed) signal(parsed);
    }

    // The basic subset of kitty's desktop notification protocol (OSC 99 ; metadata ; payload): the title and body
    // payloads (p=title, the default, and p=body), base64 payloads (e=1), chunking by identifier (i=, d=0 until
    // done) and urgency (u=, read but not shown differently). Other payload types, queries, buttons, icons, sounds
    // and close requests are ignored, and nothing is ever answered.
    private TerminalSignal? Kitty(string text, string session)
    {
        var split = text.IndexOf(';');
        if (split < 0) return null;
        var metadata = text[..split].Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2).GroupBy(pair => pair[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
        var payload = text[(split + 1)..];
        var kind = metadata.GetValueOrDefault("p", "title");
        if (kind is not ("title" or "body")) return null;
        if (metadata.GetValueOrDefault("e") == "1")
        {
            try { payload = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
            catch (FormatException) { return null; }
        }
        var id = metadata.GetValueOrDefault("i", "");
        var (title, body) = kitty.Remove(id, out var earlier) ? earlier : ("", "");
        if (kind == "title") title += payload; else body += payload;
        if (metadata.GetValueOrDefault("d") == "0")
        {
            if (kitty.Count < 16 && title.Length + body.Length < MaxOsc * 4) kitty[id] = (title, body);
            return null;
        }
        return title.Length == 0 && body.Length == 0 ? null : new(session, TerminalSignalKind.Notification) { Title = title, Body = body };
    }

    internal static TerminalSignal? Parse(string text, string session)
    {
        if (text.StartsWith("9;4;", StringComparison.Ordinal) || text == "9;4")
        {
            var parts = text.Split(';');
            if (parts.Length < 3 || !int.TryParse(parts[2], out var code) || code is < 0 or > 4) return null;
            var percent = parts.Length > 3 && int.TryParse(parts[3], out var value) ? Math.Clamp(value, 0, 100) : -1;
            return new(session, TerminalSignalKind.Progress) { Progress = (TerminalProgressState)code, Percent = code == 0 ? -1 : percent };
        }
        if (text.StartsWith("9;", StringComparison.Ordinal))
        {
            var body = text[2..];
            // Other ConEmu OSC 9 commands are a number and parameters, never a message.
            var number = body.Split(';', 2)[0];
            if (body.Length == 0 || number.Length > 0 && number.All(char.IsAsciiDigit)) return null;
            return new(session, TerminalSignalKind.Notification) { Body = body };
        }
        if (text.StartsWith("777;notify;", StringComparison.Ordinal))
        {
            var parts = text["777;notify;".Length..].Split(';', 2);
            var notification = new TerminalSignal(session, TerminalSignalKind.Notification) { Title = parts[0], Body = parts.Length > 1 ? parts[1] : "" };
            return notification.Title.Length == 0 && notification.Body.Length == 0 ? null : notification;
        }
        return null;
    }
}

// The host's record of terminal signals: the progress every session shows, which a new watcher receives first,
// and a fan-out of every later signal. A progress without a further report for 15 s is removed, as Ghostty does,
// and a session's progress ends with its shell.
internal sealed class TerminalSignalHub : IDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, TerminalSignal> progress = [];
    private readonly List<Channel<TerminalSignal>> watchers = [];
    private readonly TimeSpan timeout;
    private readonly ITimer sweep;

    internal TerminalSignalHub(TimeSpan? timeout = null, TimeProvider? time = null)
    {
        this.timeout = timeout ?? TerminalSignal.ProgressTimeout;
        Time = time ?? TimeProvider.System;
        sweep = Time.CreateTimer(_ => Sweep(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private TimeProvider Time { get; }
    private readonly Dictionary<string, DateTimeOffset> deadlines = [];

    internal void Publish(TerminalSignal signal)
    {
        lock (gate)
        {
            if (signal.Kind == TerminalSignalKind.Progress)
            {
                if (signal.Progress == TerminalProgressState.None)
                {
                    if (!progress.Remove(signal.SessionId)) return;
                    deadlines.Remove(signal.SessionId);
                }
                else
                {
                    progress[signal.SessionId] = signal;
                    deadlines[signal.SessionId] = Time.GetUtcNow() + timeout;
                    Arm();
                }
            }
            foreach (var watcher in watchers) watcher.Writer.TryWrite(signal);
        }
    }

    /// <summary>Removes a session's progress when its shell exits or closes.</summary>
    internal void End(string session) => Publish(new(session, TerminalSignalKind.Progress) { Progress = TerminalProgressState.None });

    private void Arm()
    {
        if (deadlines.Count == 0) { sweep.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        var due = deadlines.Values.Min() - Time.GetUtcNow();
        sweep.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void Sweep()
    {
        string[] stale;
        lock (gate)
        {
            var now = Time.GetUtcNow();
            stale = deadlines.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray();
        }
        foreach (var session in stale) End(session);
        lock (gate) Arm();
    }

    internal async IAsyncEnumerable<TerminalSignal> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A slow watcher loses its oldest signals rather than holding the host back.
        var channel = Channel.CreateBounded<TerminalSignal>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        TerminalSignal[] current;
        lock (gate)
        {
            current = [.. progress.Values];
            watchers.Add(channel);
        }
        try
        {
            foreach (var signal in current) yield return signal;
            await foreach (var signal in channel.Reader.ReadAllAsync(cancellationToken)) yield return signal;
        }
        finally { lock (gate) watchers.Remove(channel); }
    }

    public void Dispose() => sweep.Dispose();
}
