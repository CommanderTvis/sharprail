using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// Counts the system calls behind secure keyboard entry instead of making them: checks must never take the machine's
/// real Secure Event Input. Fails the moment this process would hold more than one reference or release one it lacks.
/// </summary>
internal sealed class RecordingSecureEventInput : ISecureEventInput
{
    private int enables, disables;
    internal int Enables => Volatile.Read(ref enables);
    internal int Disables => Volatile.Read(ref disables);
    internal int Outstanding => Enables - Disables;

    public bool Enable()
    {
        if (Interlocked.Increment(ref enables) - Disables > 1) throw new InvalidOperationException("Secure input was enabled twice without a disable in between.");
        return true;
    }

    public bool Disable()
    {
        if (Enables - Interlocked.Increment(ref disables) < 0) throw new InvalidOperationException("Secure input was disabled more often than enabled.");
        return true;
    }

    /// <summary>Installs a recording coordinator for the whole checks process.</summary>
    internal static RecordingSecureEventInput Install()
    {
        var system = new RecordingSecureEventInput();
        SecureInput.Shared = new(system);
        return system;
    }
}

internal static class SecureInputChecks
{
    internal static void Run(string root)
    {
        Coordinator();
        Views();
        Remote(root);
    }

    private static void Balanced(RecordingSecureEventInput system, SecureInput secure, string stage)
    {
        Require(system.Outstanding == secure.Held && secure.Held is 0 or 1, $"{stage}: {system.Enables} enables and {system.Disables} disables do not match {secure.Held} held.");
    }

    // Ghostty's SecureInput rules: one reference at most, wanted while active and manual or a scoped terminal wants it.
    private static void Coordinator()
    {
        var system = new RecordingSecureEventInput();
        var secure = new SecureInput(system);
        object first = new(), second = new();
        secure.SetScoped(first, true);
        Require(secure.Enabled && system.Enables == 1, "A terminal reading a password enables secure input.");
        secure.SetScoped(second, true);
        Require(system.Enables == 1, "A second terminal wanting it takes no second reference.");
        secure.SetScoped(first, false);
        Require(secure.Enabled, "Secure input stays while another terminal still wants it.");
        secure.Remove(second);
        Require(!secure.Enabled && system.Outstanding == 0, "The last terminal letting go releases the reference.");
        secure.SetScoped(first, true);
        secure.AppActive = false;
        Require(!secure.Enabled, "Deactivating the app yields secure input to other apps.");
        secure.AppActive = true;
        Require(secure.Enabled, "Reactivating takes it back while still wanted.");
        secure.Automatic = false;
        Require(!secure.Enabled, "Turning the setting off releases a password prompt's secure input.");
        secure.Manual = true;
        Require(secure.Enabled && secure.Protects(first) && secure.Protects(second), "The manual toggle enables it for the whole app regardless of the setting.");
        secure.AppActive = false; secure.AppActive = true;
        Require(secure.Enabled, "The manual toggle survives deactivation, yielding meanwhile.");
        secure.Toggle();
        Require(!secure.Enabled && !secure.Manual, "Toggling again turns it off.");
        secure.Automatic = true;
        Require(secure.Enabled, "Turning the setting back on re-claims a still-waiting prompt.");
        secure.ReleaseAll();
        Require(!secure.Enabled && system.Outstanding == 0, "Shutdown releases every reference.");
        secure.SetScoped(second, true); secure.Manual = true;
        Require(!secure.Enabled, "Nothing re-enables after shutdown released it.");
        Balanced(system, secure, "coordinator");
        // A random interleaving of every input never holds more than one reference or releases one not held.
        var random = new Random(7);
        var fuzz = new RecordingSecureEventInput();
        var fuzzed = new SecureInput(fuzz);
        var keys = new[] { new object(), new object(), new object() };
        for (var step = 0; step < 2000; step++)
        {
            switch (random.Next(5))
            {
                case 0: fuzzed.SetScoped(keys[random.Next(3)], random.Next(2) == 0); break;
                case 1: fuzzed.Remove(keys[random.Next(3)]); break;
                case 2: fuzzed.AppActive = random.Next(3) != 0; break;
                case 3: fuzzed.Automatic = random.Next(4) != 0; break;
                default: fuzzed.Toggle(); break;
            }
            Balanced(fuzz, fuzzed, "fuzz step " + step);
        }
        fuzzed.ReleaseAll();
        Require(fuzz.Outstanding == 0, "A fuzzed sequence ends balanced.");
        Require(!new SecureInput(null).Supported, "Without the system call the coordinator is unsupported, so its UI hides.");
        Console.WriteLine("PASS secure input coordinator: one reference at most, scoped terminals, activation, setting, manual toggle, shutdown, fuzzed balance");
    }

    private static void Views()
    {
        var saved = SecureInput.Shared;
        var system = RecordingSecureEventInput.Install();
        var secure = SecureInput.Shared;
        var interval = TerminalView.PasswordPollInterval;
        TerminalView.PasswordPollInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            var a = new FakeBackend(); var b = new FakeBackend();
            var viewA = new TerminalView(_ => a, new("/tmp", "secure-a", "/tmp", "client"));
            var viewB = new TerminalView(_ => b, new("/tmp", "secure-b", "/tmp", "client"));
            var other = new TextBox();
            var window = new Window { Width = 900, Height = 600, Content = new Grid { RowDefinitions = new("*,*,Auto"), Children = { viewA, viewB, other } } };
            Grid.SetRow(viewB, 1); Grid.SetRow(other, 2);
            window.Show(); window.Activate(); Settle();
            static Border Lock(TerminalView view) => view.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "TerminalSecureInput");

            a.Password = true; Settle();
            Require(!secure.Enabled, "An unfocused terminal's password prompt does not enable secure input.");
            a.FocusTerminal(); Until(() => secure.Enabled);
            Require(viewA.SecureInputShown && Lock(viewA).IsVisible && !viewB.SecureInputShown, "The focused terminal reading a password shows the lock; the other does not.");
            other.Focus(); Until(() => !secure.Enabled);
            Require(!viewA.SecureInputShown, "Focus leaving the terminal disables secure input and hides the lock.");
            a.FocusTerminal(); Until(() => secure.Enabled);
            a.Password = false; Until(() => !secure.Enabled);
            a.Password = true; Until(() => secure.Enabled);
            b.Password = true; b.FocusTerminal(); Settle();
            Require(secure.Enabled && system.Outstanding == 1 && viewB.SecureInputShown && !viewA.SecureInputShown, "Moving focus between two password prompts keeps one reference and moves the lock.");
            b.Password = false; Until(() => !secure.Enabled);
            a.FocusTerminal(); Until(() => secure.Enabled);
            secure.AppActive = false; Until(() => !secure.Enabled);
            secure.AppActive = true; Until(() => secure.Enabled);

            secure.Automatic = false; viewA.UpdateSecureInput(); Until(() => !secure.Enabled);
            Require(!viewA.SecureInputShown, "Turning the setting off disables secure input for a waiting prompt.");
            secure.Automatic = true; viewA.UpdateSecureInput(); Until(() => secure.Enabled);

            a.Exit(0); Until(() => viewA.IsExited && !secure.Enabled);
            Require(!viewA.SecureInputShown, "The shell exiting releases secure input.");

            // The Edit menu is the way to toggle_secure_input.
            Require(!secure.Manual, "The toggle starts off.");
            b.FocusTerminal(); Settle();
            var native = SharpRail.UI.ApplicationMenu.SecureKeyboardEntry(secure);
            secure.Toggle();
            Until(() => secure.Manual && secure.Enabled);
            Require(viewB.SecureInputShown, "The manually toggled terminal shows the lock while it has focus.");
            Settle();
            Require(native.IsChecked, "The Edit menu item follows the toggle.");
            secure.Toggle(); Until(() => !secure.Enabled);
            Settle();
            Require(!native.IsChecked, "The Edit menu item unchecks with the toggle.");

            b.Password = true; b.FocusTerminal(); Until(() => secure.Enabled);
            viewB.Close(); Until(() => !secure.Enabled);
            Require(system.Outstanding == 0, "Closing the tab of a waiting prompt releases secure input.");
            viewA.Dispose(); window.Close(); Settle();
            Balanced(system, secure, "views");
            Require(system.Enables > 0 && system.Outstanding == 0, "Every enable the views caused was balanced.");
        }
        finally { TerminalView.PasswordPollInterval = interval; SecureInput.Shared = saved; }
        Console.WriteLine("PASS secure input terminals: focused echo-off enables with a lock, focus loss, echo restored, two terminals, deactivation, setting off, exit, close and both toggles balance");
    }

    // A remote host reports the flag from its PTY; the client's coordinator enables secure input locally.
    private static void Remote(string root)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) { Console.WriteLine("SKIP remote secure input: PTYs require macOS or Linux."); return; }
        var saved = SecureInput.Shared;
        var system = RecordingSecureEventInput.Install();
        var secure = SecureInput.Shared;
        var workspace = Path.Combine(root, "secure-input");
        Directory.CreateDirectory(workspace);
        // The host runs off the dispatcher, which the waits below pump.
        var host = RemoteServer.Create(root, IPAddress.Loopback, 0, "secure-token");
        Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();
        try
        {
            var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemoteTerminalAdapter(address, "secure-token");
            var view = new TerminalView(launch => new SkiaTerminal(launch, remote), new(workspace, "secure-remote-" + Guid.NewGuid().ToString("N"), workspace, "client"));
            var window = new Window { Width = 900, Height = 500, Content = view };
            window.Show(); window.Activate();
            Until(() => view.Backend is { Started.IsCompletedSuccessfully: true });
            view.Backend!.FocusTerminal(); Settle();
            view.Write("printf 'SEC_%s\\n' READY\r");
            Until(() => view.ReadScreen().Contains("SEC_READY", StringComparison.Ordinal));
            Require(!secure.Enabled, "A remote shell at its prompt does not enable secure input.");
            view.Write("sh -c 'stty -echo; read secret; stty echo; echo SEC_DONE'\r");
            Until(() => secure.Enabled && view.SecureInputShown);
            view.Write("hidden\r");
            Until(() => view.ReadScreen().Contains("SEC_DONE", StringComparison.Ordinal) && !secure.Enabled);
            view.Close(); window.Close(); Settle();
            Require(system.Enables >= 1 && system.Outstanding == 0, "The remote prompt's secure input was taken locally and released.");
        }
        finally
        {
            SecureInput.Shared = saved;
            Task.Run(async () => { await host.StopAsync(); await host.DisposeAsync(); }).GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS remote secure input: the host's echo-off flag enables secure input on the client and restored echo releases it");
    }

    private sealed class FakeBackend : Border, ITerminalBackend
    {
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Password;
        internal FakeBackend() => Focusable = true;
        internal void Exit(int code) => exited.TrySetResult(code);
        public Control View => this;
        public Task Started => Task.CompletedTask;
        public Task<int> Exited => exited.Task;
        public Task Detached => new TaskCompletionSource().Task;
        public bool Yielded => false;
        public bool Watching => false;
        public ValueTask<bool> IsBusyAsync() => ValueTask.FromResult(false);
        public ValueTask<bool> IsPasswordInputAsync() => ValueTask.FromResult(Password && !exited.Task.IsCompleted);
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public void FocusTerminal() => Focus();
        public void Write(string data) { }
        public string ReadScreen() => "";
        public void SetAgentNewline(bool enabled) { }
        public void Dispose() { }
    }
}