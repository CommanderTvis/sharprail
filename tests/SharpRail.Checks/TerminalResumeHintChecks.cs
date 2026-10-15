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
        foreach (var (prefill, watching) in new (TerminalPrefill?, bool)[] { (null, false), (new("resume fixture", true), false), (new("", false), false), (new("resume fixture", false), true) })
        {
            using var fixture = new Fixture(prefill, watching);
            fixture.Session.Output("prompt> ");
            if (!watching && prefill is not null) Until(() => fixture.Session.Writes.Length > 0);
            Settle();
            Require(!fixture.Hint.IsVisible, "Ordinary, submitted, empty and read-only attachments must not show a resume hint.");
            fixture.View.Write("ordinary paste");
            Settle();
            Require(!fixture.Hint.IsVisible, "Ordinary pasted commands must never create the resume hint.");
        }
        using (var fixture = new Fixture(new("resume fixture", false), blocked: true))
        {
            var body = fixture.Backend.Bounds;
            Require(fixture.Other.Focus(), "The other input takes focus before the command is delivered.");
            fixture.Session.Output("prompt> ");
            Until(() => fixture.Session.WriteStarted.Task.IsCompleted);
            Require(!fixture.Hint.IsVisible, "The hint waits for the actual prefill write acknowledgement.");
            fixture.Session.Acknowledge.TrySetResult();
            Until(() => fixture.Hint.IsVisible);
            Require(fixture.Session.Text == "resume fixture" && fixture.Other.IsFocused && fixture.Backend.Bounds == body && !fixture.Hint.IsHitTestVisible,
                "The overlay neither submits the command, steals focus, intercepts input nor resizes the terminal grid.");
            fixture.Session.Output("\u001b[6n");
            Until(() => fixture.Session.Text.Contains("R", StringComparison.Ordinal));
            Require(fixture.Hint.IsVisible, "Automatic terminal query replies must not dismiss the hint.");
            fixture.Backend.FocusTerminal();
            fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            fixture.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Until(() => !fixture.Hint.IsVisible && fixture.Session.Text.Contains('\r'));
            Require(fixture.Session.Text.Count(character => character == '\r') == 1, "Enter reaches the shell exactly once; the hint never sends an extra Return.");
            fixture.Backend.ResumeHint.Offer(); Settle();
            Require(!fixture.Hint.IsVisible, "A late delivery notification must not resurrect a consumed offer.");
        }
        using (var fixture = new Fixture(new("resume fixture", false), blocked: true))
        {
            fixture.Session.Output("prompt> ");
            Until(() => fixture.Session.WriteStarted.Task.IsCompleted);
            fixture.Backend.Terminal.SendKey(Key.Enter);
            fixture.Session.Acknowledge.TrySetResult();
            Until(() => fixture.Session.Writes.Length >= 2);
            Settle();
            Require(!fixture.Hint.IsVisible, "Enter before a delayed acknowledgement suppresses the late hint.");
        }
        foreach (var action in new Action<Fixture>[] { fixture => fixture.Backend.Terminal.SendKey(Key.Back), fixture => fixture.Backend.Terminal.SendKey(Key.C, KeyModifiers.Control), fixture => fixture.View.Write("edited"), fixture => fixture.Window.Content = new TextBlock { Text = "Another tab" }, fixture => fixture.View.Dispose() })
        {
            using var fixture = new Fixture(new("resume fixture", false));
            fixture.Session.Output("prompt> "); Until(() => fixture.Hint.IsVisible);
            action(fixture); Settle();
            Require(!fixture.Hint.IsVisible, "Editing, cancel, detach and disposal dismiss the offered-command hint.");
        }
        Console.WriteLine("PASS terminal resume hint: actual offered-write acknowledgement, no auto-submit/focus/grid changes, query replies, exactly-once Enter, late acknowledgement, edits/cancel/detach and read-only exclusion");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Session Session;
        internal readonly SkiaTerminal Backend;
        internal readonly TerminalView View;
        internal readonly Window Window;
        internal readonly TextBox Other = new();
        internal readonly Control Hint;
        internal Fixture(TerminalPrefill? prefill, bool watching = false, bool blocked = false)
        {
            Session = new(prefill, watching, blocked);
            SkiaTerminal? backend = null;
            View = new(launch => backend = new SkiaTerminal(launch, new Service(Session)), new("/tmp", "hint-fixture", "/tmp", "client"));
            Backend = backend!;
            Hint = View.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalResumeHint");
            Window = new() { Width = 900, Height = 500, Content = new Grid { RowDefinitions = new("*,Auto"), Children = { View, Other } } };
            Grid.SetRow(Other, 1);
            Window.Show(); Until(() => Backend.Started.IsCompletedSuccessfully && Backend.Bounds.Height > 0); Settle();
        }
        public void Dispose() { View.Dispose(); Window.Close(); }
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