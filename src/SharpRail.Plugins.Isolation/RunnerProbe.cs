namespace SharpRail.Plugins.Isolation;

/// <summary>
/// The members of the runner's <c>probe echo</c> mode, which exercises the channel and the process with no plugin
/// loaded: the checks drive it now, and the packaged gates will start the shipped runner the same way.
/// </summary>
public static class RunnerProbe
{
    public const string EchoMode = "echo", AvaloniaPrivateMode = "avalonia-private";
    /// <summary>What <c>probe avalonia-private</c> prints when the stub platform registers and resolves.</summary>
    public const string AvaloniaPrivateResolved = "avalonia-private: a stub IWindowingPlatform registered and resolved";
    public const string StandardOutputLine = "probe: standard output", StandardErrorLine = "probe: standard error";

    /// <summary>Answers its payload.</summary>
    public static readonly int Echo = IsoMember.Id("probe.echo");
    /// <summary>Waits the payload's milliseconds, counting a cancellation it observes.</summary>
    public static readonly int Delay = IsoMember.Id("probe.delay");
    /// <summary>How many calls the runner saw cancelled.</summary>
    public static readonly int Cancelled = IsoMember.Id("probe.cancelled");
    /// <summary>Lends a handle and answers its id.</summary>
    public static readonly int Mint = IsoMember.Id("probe.mint");
    /// <summary>A call on a lent handle: answers <c>live:&lt;id&gt;</c>.</summary>
    public static readonly int OnHandle = IsoMember.Id("probe.on-handle");
    /// <summary>Streams the payload's count of items, "0" upward.</summary>
    public static readonly int Stream = IsoMember.Id("probe.stream");
    /// <summary>How many items the current stream has handed to the channel.</summary>
    public static readonly int Sent = IsoMember.Id("probe.sent");
    /// <summary>Writes the payload into the descriptor the call carries.</summary>
    public static readonly int WriteDescriptor = IsoMember.Id("probe.write-descriptor");
    /// <summary>Sends a descriptor back, which the host must refuse.</summary>
    public static readonly int SendDescriptor = IsoMember.Id("probe.send-descriptor");
    /// <summary>Calls the host's <see cref="HostEcho"/> on the handle the payload names; answers the reply or the error.</summary>
    public static readonly int CallHost = IsoMember.Id("probe.call-host");
    /// <summary>The host member the runner calls back.</summary>
    public static readonly int HostEcho = IsoMember.Id("probe.host-echo");
    /// <summary>Sends a call numbered as the host's, which the host must refuse.</summary>
    public static readonly int BadParity = IsoMember.Id("probe.bad-parity");
    /// <summary>A one-way the runner records in arrival order.</summary>
    public static readonly int Note = IsoMember.Id("probe.note");
    /// <summary>The recorded notes, comma-separated.</summary>
    public static readonly int Notes = IsoMember.Id("probe.notes");
    /// <summary>Starts a child that sleeps for minutes, ignoring every signal but a kill, and answers its pid.</summary>
    public static readonly int SpawnChild = IsoMember.Id("probe.spawn-child");
    /// <summary>Exits with the payload's code.</summary>
    public static readonly int Exit = IsoMember.Id("probe.exit");
}