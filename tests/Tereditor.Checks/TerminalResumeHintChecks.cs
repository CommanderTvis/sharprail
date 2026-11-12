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
        Companion();
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

    // A long drawing in a narrow column never takes the column: the surface keeps its minimum, the companion its bounded share, the drawing scrolls inside.
    private static void StackedBounds(Fixture fixture, TerminalView view, ContentControl body, Border pane, string when)
    {
        var scroller = new ScrollViewer { Content = new StackPanel { Children = { new TextBlock { Text = string.Join('\n', Enumerable.Range(0, 200).Select(line => "markdown line " + line)) } } } };
        pane.Child = scroller;
        foreach (var height in new[] { 500.0, 360.0 })
        {
            fixture.Window.Height = height; Settle();
            var share = pane.Bounds.Height / view.Bounds.Height;
            Require(view.CompanionStacked && pane.TranslatePoint(default, view) is { } origin && origin.Y >= body.Bounds.Bottom - 1 && body.Bounds.Height >= TerminalView.StackedSurfaceMinHeight - 1 && share <= TerminalView.CompanionStackMaxShare + 0.01 && pane.Bounds.Height >= TerminalView.StackedCompanionMinHeight - 1,
                $"At {when} width and height {height} the surface keeps {body.Bounds.Height} (min {TerminalView.StackedSurfaceMinHeight}) and the companion {pane.Bounds.Height} of {view.Bounds.Height} (max share {TerminalView.CompanionStackMaxShare}).");
            Require(scroller.Extent.Height > scroller.Viewport.Height && Math.Abs(scroller.Bounds.Height - pane.Bounds.Height) < 1, "The long drawing scrolls inside the companion's own area.");
        }
        fixture.Window.Height = 500; Settle();
        pane.Child = null;
    }

    // The companion beside the surface: 45% on first opening, a restored width, stacked below the surface when the view is narrow.
    private static void Companion()
    {
        using var fixture = new Fixture(false, null);
        var view = fixture.View;
        var saved = new List<double>();
        view.CompanionWidthChanged += saved.Add;
        var body = view.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "TerminalBody");
        var pane = new Border { Name = "CheckCompanion" };
        view.Companion = pane; Settle();
        Require(!view.CompanionStacked && Math.Abs(pane.Bounds.Width / view.Bounds.Width - 0.45) < 0.02, $"The first opening takes 45%: {pane.Bounds.Width} of {view.Bounds.Width}.");
        Require(body.Bounds.Height > 300 && Math.Abs(pane.Bounds.Height - body.Bounds.Height) < 1, "Wide, the companion sits beside the surface at its full height.");
        view.CompanionWidth = 300; Settle();
        Require(Math.Abs(pane.Bounds.Width - 300) < 1 && Math.Abs(body.Bounds.Width + 300 - view.Bounds.Width) < 12, $"A restored width applies to the open companion: pane {pane.Bounds.Width}, body {body.Bounds.Width}, view {view.Bounds.Width}.");
        fixture.Window.Width = 400; Settle();
        Require(view.CompanionStacked && Math.Abs(pane.Bounds.Width - view.Bounds.Width) < 1 && Math.Abs(body.Bounds.Width - view.Bounds.Width) < 1,
            "A narrow view stacks the companion below the surface at the full width.");
        StackedBounds(fixture, view, body, pane, "narrow");
        fixture.Window.Width = 900; Settle();
        Require(!view.CompanionStacked && Math.Abs(pane.Bounds.Width - 300) < 1, "A wide view returns the companion to its column at the saved width.");
        view.Companion = null; Settle();
        Require(saved.Count == 1 && Math.Abs(saved[0] - 300) < 1 && view.CompanionWidth is { } kept && Math.Abs(kept - 300) < 1, "Closing reports the width and keeps it.");
        Require(Math.Abs(body.Bounds.Width - view.Bounds.Width) < 1, "Closing returns the full width to the surface.");
        fixture.Window.Width = 400; Settle();
        view.Companion = pane; Settle();
        Require(view.CompanionStacked && view.CompanionWidth is { } narrow && Math.Abs(narrow - 300) < 1, "Opening while narrow stacks the companion and still remembers the width.");
        view.Companion = null; Settle();
        Require(!view.CompanionStacked && saved.Count == 1, "Closing a stacked companion reports no width.");
        // A hairline sits between the chips and the content above only while a companion is open, without moving anything.
        var chips = view.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == "TerminalAccessories");
        var divider = view.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "TerminalAccessoriesDivider");
        chips.Children.Add(new Border { Height = 24 }); Settle();
        var chipHeight = chips.Bounds.Height; var bodyHeight = body.Bounds.Height;
        Require(!divider.IsVisible, "Without a companion the chip strip is borderless.");
        view.Companion = pane; Settle();
        Require(divider.IsVisible && ReferenceEquals(divider.Background, SharpRail.Plugins.UI.Kit.Ui.BorderBrush) && Math.Abs(divider.Bounds.Height - 1) < 0.01 &&
            Math.Abs(divider.Bounds.Top - chips.Bounds.Top) < 0.5 && Math.Abs(chips.Bounds.Height - chipHeight) < 0.5,
            $"An open companion shows a 1px border on the chip strip's top edge without resizing it: {divider.Bounds}, strip {chips.Bounds}.");
        view.Companion = null; Settle();
        Require(!divider.IsVisible && Math.Abs(chips.Bounds.Height - chipHeight) < 0.5 && Math.Abs(body.Bounds.Height - bodyHeight) < 0.5, "Closing removes the border and restores the sizes.");
        chips.Children.Clear(); Settle();
        // The strip row above the surface: the tab's top row, a list only with several items, pressing it toggles.
        var presses = 0;
        var chosen = -1;
        var one = SharpRail.UI.Plugins.CompanionStrip.Create("CheckStrip", SharpRail.Plugins.UI.Kit.Ui.Icon("close"), "Drawing", "visualization", ["Drawing"], false, () => presses++, null);
        view.CompanionStrips.Children.Add(one); Settle();
        Require(one.TranslatePoint(new Point(0, 0), view) is { } corner && Math.Abs(corner.X) < 0.5 && Math.Abs(corner.Y) < 0.5 && Math.Abs(one.Bounds.Width - view.Bounds.Width) < 0.5 &&
            Math.Abs(one.Bounds.Height - SharpRail.Plugins.UI.Kit.LayoutRoles.PaneHeaderStrip) < 0.5 && one.BorderThickness.Bottom == 1 && Math.Abs(body.Bounds.Top - one.Bounds.Height) < 0.5,
            $"The strip sits at the very top, full width, one slim row high with a divider; the surface starts below it: {one.Bounds}, surface at {body.Bounds.Top}.");
        Require(one.GetLogicalDescendants().OfType<TextBlock>().Last().Text == "· 1 visualization" && !one.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "CheckStripItems"), "One item reads as a singular count without a list.");
        one.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CheckStripToggle").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(presses == 1, "Pressing the strip toggles the companion.");
        view.CompanionStrips.Children.Clear();
        var several = SharpRail.UI.Plugins.CompanionStrip.Create("CheckStrip", SharpRail.Plugins.UI.Kit.Ui.Icon("close"), "First", "visualization", ["First", "Second", "Third"], false, () => presses++, index => chosen = index);
        view.CompanionStrips.Children.Add(several); Settle();
        var chevron = several.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CheckStripItems");
        var menu = (MenuFlyout)chevron.Flyout!;
        Require(several.GetLogicalDescendants().OfType<TextBlock>().Last().Text == "· 3 visualizations" && menu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(["First", "Second", "Third"]), "Several items read as a plural count with a list of their titles.");
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Require(chosen == 2, "Choosing an item reports its index.");
        view.CompanionStrips.Children.Clear(); Settle();
        Require(Math.Abs(body.Bounds.Top) < 0.5, "Without a strip there is no gap above the surface.");
        Console.WriteLine("PASS terminal companion: first opening takes 45%, a saved width is restored, a narrow view stacks it below within bounded shares, a hairline separates it from the chips");
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