using System.Runtime.InteropServices;

namespace SharpRail.UI.Terminal;

/// <summary>The system call behind secure keyboard entry; checks substitute a counting fake.</summary>
public interface ISecureEventInput
{
    /// <summary>True when the system took one more reference.</summary>
    bool Enable();
    /// <summary>True when the system released one reference.</summary>
    bool Disable();
}

/// <summary>
/// macOS Secure Event Input, modelled on Ghostty's <c>SecureInput</c> singleton: while it is on, keyboard events reach only
/// the focused application and no event tap can read them. The system keeps one reference count for every process, so
/// each enable is balanced by exactly one disable, and this coordinator holds at most one reference at a time.
/// It wants the reference while the app is active and either the manual toggle is on or, with the automatic setting on,
/// a focused terminal reads a password; it yields on deactivation and takes it back on reactivation, as Ghostty does.
/// <see cref="ReleaseAll"/> runs on every exit path so a reference never outlives the app. UI thread, except
/// <see cref="ReleaseAll"/>, which any thread may call.
/// </summary>
public sealed class SecureInput
{
    private readonly ISecureEventInput? system;
    private readonly Lock gate = new();
    private readonly Dictionary<object, bool> scoped = new(ReferenceEqualityComparer.Instance);
    private bool manual, automatic = true, appActive = true, released;
    private int held;

    public SecureInput(ISecureEventInput? system) => this.system = system;

    /// <summary>The app's coordinator: the Carbon call on macOS, nothing anywhere else. Checks replace it with a fake-backed one.</summary>
    public static SecureInput Shared { get; internal set; } = new(
#if !ANDROID && !BROWSER
        OperatingSystem.IsMacOS() ? new CarbonSecureEventInput() : null
#else
        null
#endif
    );

    /// <summary>False where the platform has no secure keyboard entry; the UI hides its items.</summary>
    public bool Supported => system is not null;
    /// <summary>True while this app holds a system reference.</summary>
    public bool Enabled { get { lock (gate) return held > 0; } }
    /// <summary>The references this app holds: zero or one, never more.</summary>
    public int Held { get { lock (gate) return held; } }
    /// <summary>Raised after <see cref="Enabled"/> or <see cref="Manual"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Ghostty's <c>toggle_secure_input</c>: on for the whole app until toggled off or the app quits.</summary>
    public bool Manual
    {
        get { lock (gate) return manual; }
        set { lock (gate) manual = value; Apply(); }
    }

    /// <summary>The "Secure input for password prompts" setting; off ignores every terminal's password state.</summary>
    public bool Automatic
    {
        get { lock (gate) return automatic; }
        set { lock (gate) automatic = value; Apply(); }
    }

    /// <summary>Whether one of the app's windows is active; secure input is yielded to other apps while it is not.</summary>
    public bool AppActive
    {
        get { lock (gate) return appActive; }
        set { lock (gate) appActive = value; Apply(); }
    }

    public void Toggle() => Manual = !Manual;

    /// <summary>Records whether a terminal currently wants secure input: focused, in an active window, reading a password.</summary>
    public void SetScoped(object terminal, bool wanted)
    {
        lock (gate)
        {
            if (scoped.TryGetValue(terminal, out var previous) && previous == wanted) return;
            scoped[terminal] = wanted;
        }
        Apply();
    }

    /// <summary>Forgets a terminal that closed, exited or detached.</summary>
    public void Remove(object terminal)
    {
        lock (gate) if (!scoped.Remove(terminal)) return;
        Apply();
    }

    /// <summary>Whether this terminal is the reason secure input is on (for its lock indicator).</summary>
    public bool Protects(object terminal)
    {
        lock (gate) return held > 0 && (manual || automatic && scoped.GetValueOrDefault(terminal));
    }

    /// <summary>Gives back every reference and refuses new ones: shutdown, crash and process-exit paths.</summary>
    public void ReleaseAll()
    {
        bool changed;
        lock (gate)
        {
            released = true;
            changed = held > 0;
            while (held > 0 && system!.Disable()) held--;
            // A failed disable leaves nothing more to try; the system frees a dead process's references.
            held = 0;
        }
        if (changed) Changed?.Invoke();
    }

    private void Apply()
    {
        lock (gate)
        {
            var desired = system is not null && !released && appActive && (manual || automatic && scoped.ContainsValue(true));
            if (desired && held == 0 && system!.Enable()) held = 1;
            else if (!desired && held > 0 && system!.Disable()) held = 0;
        }
        // Raised even when the system state stays, so the toggle's check mark and the indicators follow every change.
        Changed?.Invoke();
    }
}

#if !ANDROID && !BROWSER
/// <summary>Follows the desktop app's activation and gives every reference back however the process ends.</summary>
public static class SecureInputLifetime
{
    public static void Install(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop, SecureInput secure)
    {
        if (!secure.Supported) return;
        // Deactivating one window precedes activating the next, so the answer is read once both have run.
        void Refresh(object? sender, EventArgs e) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => secure.AppActive = desktop.Windows.Any(window => window.IsActive));
        Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((window, _) =>
        {
            window.Activated += Refresh;
            window.Deactivated += Refresh;
        });
        desktop.Exit += (_, _) => secure.ReleaseAll();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => secure.ReleaseAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => secure.ReleaseAll();
    }
}

internal sealed class CarbonSecureEventInput : ISecureEventInput
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    [DllImport(Carbon)] private static extern int EnableSecureEventInput();
    [DllImport(Carbon)] private static extern int DisableSecureEventInput();

    public bool Enable() => EnableSecureEventInput() == 0;
    public bool Disable() => DisableSecureEventInput() == 0;
}
#endif