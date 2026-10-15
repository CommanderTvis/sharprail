using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

internal interface ITerminalResumeHint
{
    TerminalResumeHint ResumeHint { get; }
    bool NativeResumeHint => false;
}

internal sealed class TerminalResumeHint
{
    private readonly Lock gate = new();
    private bool visible, dismissed;
    internal event Action? Changed;
    internal bool Visible { get { lock (gate) return visible; } }
    internal static bool IsOffer(TerminalPrefill? prefill) => prefill is { Submit: false, Text.Length: > 0 };
    internal void Delivered(TerminalPrefill? prefill) { if (IsOffer(prefill)) Offer(); }
    internal void Offer() => Update(true, false);
    internal void Dismiss() => Update(false, true);
    private void Update(bool value, bool dismiss)
    {
        lock (gate)
        {
            dismissed |= dismiss;
            value &= !dismissed;
            if (visible == value) return;
            visible = value;
        }
        Changed?.Invoke();
    }
}