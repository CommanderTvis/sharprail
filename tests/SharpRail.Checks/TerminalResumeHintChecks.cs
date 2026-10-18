using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class TerminalResumeHintChecks
{
    internal static void Run()
    {
        Run(false);
        InputHold();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) Run(true);
        else Console.WriteLine("SKIP native resume-hint input: libghostty-vt is unavailable on this platform; portable overlay checks passed.");
    }

    private static void Run(bool native)
    {
        foreach (var (prefill, watching) in new (TerminalPrefill?, bool)[] { (null, false), (new("resume fixture", true), false), (new("", false), false), (new("resume fixture", false), true) })
        {
            using var fixture = new Fixture(native, prefill, watching);
            fixture.Session.Output("prompt> ");
            if (!watching && prefill is not null) Until(() => fixture.Session.Writes.Length > 0);
            Settle();
            Require(!fixture.Hint.IsVisible, "Ordinary, submitted, empty and read-only attachments must not show a resume hint.");
            fixture.View.Write("ordinary paste");
            Settle();
            Require(!fixture.Hint.IsVisible, "Ordinary pasted commands must never create the resume hint.");
        }
        using (var fixture = new Fixture(native, new("resume fixture", false), blocked: true))
        {
            var body = fixture.Backend.View.Bounds;
            Require(fixture.Other.Focus(), "The other input takes focus before the command is delivered.");
            fixture.Session.Output("prompt> ");
            Until(() => fixture.Session.WriteStarted.Task.IsCompleted);
            Require(!fixture.Hint.IsVisible, "The hint waits for the actual prefill write acknowledgement.");
            fixture.Session.Acknowledge.TrySetResult();
            Until(() => fixture.Hint.IsVisible);
            Require(fixture.Session.Text == "resume fixture" && fixture.Other.IsFocused && fixture.Backend.View.Bounds == body && !fixture.Hint.IsHitTestVisible,
                "The overlay neither submits the command, steals focus, intercepts input nor resizes the terminal grid.");
            fixture.Session.Output("\u001b[6n");
            Until(() => fixture.Session.Text.Contains("R", StringComparison.Ordinal));
            Require(fixture.Hint.IsVisible, "Automatic terminal query replies must not dismiss the hint.");
            fixture.Backend.FocusTerminal();
            if (native)
            {
                fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                fixture.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            }
            else fixture.SendKey(Key.Enter);
            Until(() => !fixture.Hint.IsVisible && fixture.Session.Text.Contains('\r'));
            Require(fixture.Session.Text.Count(character => character == '\r') == 1, "Enter reaches the shell exactly once; the hint never sends an extra Return.");
            fixture.Signal.Offer(); Settle();
            Require(!fixture.Hint.IsVisible, "A late delivery notification must not resurrect a consumed offer.");
        }
        using (var fixture = new Fixture(native, new("resume fixture", false), blocked: true))
        {
            fixture.Session.Output("prompt> ");
            Until(() => fixture.Session.WriteStarted.Task.IsCompleted);
            fixture.SendKey(Key.Enter);
            fixture.Session.Acknowledge.TrySetResult();
            Until(() => fixture.Session.Writes.Length >= 2);
            Settle();
            Require(!fixture.Hint.IsVisible, "Enter before a delayed acknowledgement suppresses the late hint.");
        }
        foreach (var action in new Action<Fixture>[] { fixture => fixture.SendKey(Key.Back), fixture => fixture.SendKey(Key.C, KeyModifiers.Control), fixture => fixture.View.Write("edited"), fixture => fixture.Window.Content = new TextBlock { Text = "Another tab" }, fixture => fixture.View.Dispose() })
        {
            using var fixture = new Fixture(native, new("resume fixture", false));
            fixture.Session.Output("prompt> "); Until(() => fixture.Hint.IsVisible);
            action(fixture); Settle();
            Require(!fixture.Hint.IsVisible, "Editing, cancel, detach and disposal dismiss the offered-command hint.");
        }
        Console.WriteLine($"PASS {(native ? "native" : "portable")} terminal resume hint: actual offered-write acknowledgement, no auto-submit/focus/grid changes, query replies, exactly-once Enter, late acknowledgement, edits/cancel/detach and read-only exclusion");
    }

    // A plugin typing a command into an agent holds the user's keys out of the terminal; every way out lets go.
    private static void InputHold()
    {
        using (var fixture = new Fixture(false, null))
        {
            var seen = 0;
            fixture.Backend.View.KeyDown += (_, _) => seen++;
            var indicator = fixture.View.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalInputHeld");
            void Press(Key key = Key.A) { fixture.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.A, null); fixture.Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.A, null); Settle(); }
            fixture.Backend.FocusTerminal(); Settle();
            Press();
            Require(seen == 1 && !fixture.View.InputHeld && !indicator.IsVisible, "Keys reach the terminal when nothing holds it.");
            var first = fixture.View.HoldInput();
            var second = fixture.View.HoldInput();
            Settle();
            Press();
            Require(fixture.View.InputHeld && seen == 1 && indicator.IsVisible, "A held terminal drops the user's keys and says so.");
            fixture.View.Write("driven");
            Require(fixture.Session.Text.Contains("driven", StringComparison.Ordinal), "A hold never blocks the driver's own writes.");
            first.Dispose(); first.Dispose(); Settle();
            Require(fixture.View.InputHeld, "Holds nest: one handle ending leaves the other in force, and disposing twice counts once.");
            second.Dispose(); Settle();
            Press();
            Require(!fixture.View.InputHeld && seen == 2 && !indicator.IsVisible, "Ending the last hold returns the keyboard.");

            var restarted = fixture.View.HoldInput();
            fixture.View.Restart(); Settle();
            Require(!fixture.View.InputHeld, "Restarting the terminal ends a hold, so a driver that never returns cannot leave it locked.");
            restarted.Dispose(); Settle();
            Require(!fixture.View.InputHeld, "A stale handle disposed after a restart does not release a later hold.");
            var later = fixture.View.HoldInput();
            restarted.Dispose();
            Require(fixture.View.InputHeld, "A handle from before a restart leaves a later hold alone.");
            later.Dispose();

            var saved = TerminalView.MaxInputHold;
            TerminalView.MaxInputHold = TimeSpan.FromMilliseconds(100);
            try
            {
                var lapsed = fixture.View.HoldInput();
                Until(() => !fixture.View.InputHeld);
                Require(!fixture.View.InputHeld, "A hold ends by itself when its driver never disposes it.");
                lapsed.Dispose();
            }
            finally { TerminalView.MaxInputHold = saved; }

            var disposing = fixture.View.HoldInput();
            fixture.View.Dispose(); Settle();
            Require(!fixture.View.InputHeld, "Closing the tab ends a hold.");
            disposing.Dispose();
        }
        Console.WriteLine("PASS terminal input hold: keys dropped during a driven entry, writes unaffected, nesting, restart, time limit and close release it");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Session Session;
        internal ITerminalBackend Backend => View.Backend!;
        internal TerminalResumeHint Signal => ((ITerminalResumeHint)Backend).ResumeHint;
        internal void SendKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            if (Backend is SkiaTerminal skia) skia.Terminal.SendKey(key, modifiers);
            else ((PortableBackend)Backend).SendKey(key, modifiers);
        }
        internal readonly TerminalView View;
        internal readonly Window Window;
        internal readonly TextBox Other = new();
        internal readonly Control Hint;
        internal Fixture(bool native, TerminalPrefill? prefill, bool watching = false, bool blocked = false)
        {
            Session = new(prefill, watching, blocked);
            View = new(launch => native ? new SkiaTerminal(launch, new Service(Session)) : new PortableBackend(Session), new("/tmp", "hint-fixture", "/tmp", "client"));
            Hint = View.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalResumeHint");
            Window = new() { Width = 900, Height = 500, Content = new Grid { RowDefinitions = new("*,Auto"), Children = { View, Other } } };
            Grid.SetRow(Other, 1);
            Window.Show(); Until(() => Backend.Started.IsCompletedSuccessfully && Backend.View.Bounds.Height > 0); Settle();
        }
        public void Dispose() { View.Dispose(); Window.Close(); }
    }
    // The overlay and offer lifetime are portable; real keyboard/VT plumbing remains exercised above on native platforms.
    private sealed class PortableBackend : Border, ITerminalBackend, ITerminalResumeHint
    {
        private readonly Session session;
        private readonly CancellationTokenSource lifetime = new();
        public TerminalResumeHint ResumeHint { get; } = new();
        public Control View => this;
        public Task Started => Task.CompletedTask;
        public Task<int> Exited => session.Exit;
        public Task Detached => session.Detached;
        public bool Watching => session.Watching;
        public bool Yielded => Watching;
        internal PortableBackend(Session session)
        {
            this.session = session;
            Focusable = true;
            _ = Read();
        }
        private async Task Read()
        {
            var prefill = session.Prefill;
            try
            {
                await foreach (var bytes in session.ReadAsync(lifetime.Token))
                {
                    if (Encoding.UTF8.GetString(bytes.Span) == "\e[6n" && !Watching)
                        await session.WriteAsync(Encoding.UTF8.GetBytes("\e[1;1R"), lifetime.Token);
                    if (prefill is not null && !bytes.IsEmpty && !Watching)
                    {
                        await session.WriteAsync(Encoding.UTF8.GetBytes(prefill.Text + (prefill.Submit ? "\r" : "")), lifetime.Token);
                        ResumeHint.Delivered(prefill);
                        prefill = null;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        internal void SendKey(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            Write(key == Key.Enter ? "\r" : key == Key.Back ? "\b" : key == Key.C && modifiers.HasFlag(KeyModifiers.Control) ? "\u0003" : throw new ArgumentException("Unsupported fixture input."));
        public void Write(string data)
        {
            if (Watching) return;
            if (data.Length > 0) ResumeHint.Dismiss();
            session.WriteAsync(Encoding.UTF8.GetBytes(data), lifetime.Token).GetAwaiter().GetResult();
        }
        public void FocusTerminal() => Focus();
        public string ReadScreen() => "";
        public void SetAgentNewline(bool enabled) { }
        public ValueTask<bool> IsBusyAsync() => ValueTask.FromResult(false);
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public void Dispose() { ResumeHint.Dismiss(); lifetime.Cancel(); }
    }
    private sealed class Service(Session session) : ITerminalService
    {
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<ITerminalSession>(session);
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    private sealed class Session(TerminalPrefill? prefill, bool watching, bool blocked) : ITerminalSession
    {
        private readonly Channel<ReadOnlyMemory<byte>> output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly List<string> writes = [];
        internal readonly TaskCompletionSource WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Acknowledge = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[] Writes { get { lock (writes) return writes.ToArray(); } }
        internal string Text => string.Concat(Writes);
        internal void Output(string text) => output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public string Id => "hint-fixture";
        public bool Created => true;
        public ReadOnlyMemory<byte> Replay => Encoding.UTF8.GetBytes("recorded screen\r\n");
        public long Position => 0;
        public bool Watching => watching;
        public (int Columns, int Rows) Grid => (80, 24);
        public TerminalPrefill? Prefill => prefill;
        public Task<int> Exit => new TaskCompletionSource<int>().Task;
        public Task Detached => new TaskCompletionSource().Task;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await foreach (var value in output.Reader.ReadAllAsync(cancellationToken)) yield return value; }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            if (text.StartsWith("resume fixture", StringComparison.Ordinal))
            {
                WriteStarted.TrySetResult();
                if (blocked) await Acknowledge.Task.WaitAsync(cancellationToken);
            }
            lock (writes) writes.Add(text);
        }
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask KillAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}