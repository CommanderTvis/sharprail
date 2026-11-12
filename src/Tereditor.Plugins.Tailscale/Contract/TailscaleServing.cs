namespace SharpRail.Plugins.Tailscale;

/// <summary>The builtin plugin's bridge to its owner's listener; it never owns the host itself.</summary>
public sealed class TailscaleServing
{
    public Func<string, CancellationToken, ValueTask<string>>? EnsureListening { get; set; }
    private Func<string, bool>? authenticate;
    public Func<string, bool>? Authenticate { get => Volatile.Read(ref authenticate); set => Volatile.Write(ref authenticate, value); }
    public Func<ValueTask>? StopListening { get; set; }
}