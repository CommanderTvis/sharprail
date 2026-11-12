using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>A terminal's own font size: zoom gestures over it, Ghostty's per-surface semantics and the grid it leaves.</summary>
internal static class TerminalFontZoomChecks
{
    internal static void Run()
    {
        var session = new Session(watching: false);
        var (view, window) = Show(session, "font-a", yield: false);
        var skia = (SkiaTerminal)view.Backend!;
        var baseSize = skia.Terminal.FontSize;
        var grid = skia.Terminal.Size;
        var surface = skia.Terminal;

        Wheel(window, surface, 1, KeyModifiers.None);
        Require(view.FontSizeOffset == 0 && skia.Terminal.FontSize == baseSize, "A plain wheel scrolls the terminal and leaves its font alone.");
        Wheel(window, surface, 2, KeyModifiers.Control);
        Settle();
        Require(view.FontSizeOffset == 2 && skia.Terminal.FontSize == baseSize + 2, $"Control-wheel steps the font a point per notch: {skia.Terminal.FontSize}.");
        Require(skia.Terminal.Size.Columns < grid.Columns && skia.Terminal.Size.Rows < grid.Rows, "A larger font refits the grid to fewer cells.");
        Until(() => session.LastResize == (skia.Terminal.Size.Columns, skia.Terminal.Size.Rows));
        Wheel(window, surface, -0.4, KeyModifiers.Meta); Wheel(window, surface, -0.4, KeyModifiers.Meta);
        Require(view.FontSizeOffset == 2, "A trackpad's fractional wheel steps accumulate before moving a point.");
        Wheel(window, surface, -0.4, KeyModifiers.Meta);
        Require(view.FontSizeOffset == 1, "Command-wheel takes the same path once the fractions make a whole point.");

        view.Magnify(0.05); view.Magnify(0.04);
        Require(view.FontSizeOffset == 1, "Magnification accumulates below a tenth.");
        Magnify(window, surface, 0.11);
        Require(view.FontSizeOffset == 3, $"A pinch over the surface steps a point per tenth of magnification: {view.FontSizeOffset}.");
        Magnify(window, surface, -5);
        Require(view.FontSizeOffset == TerminalView.MinFontSizeOffset, "The font never shrinks past its bound.");

        var accessory = new Button { Content = "accessory" };
        view.Accessories.Children.Add(accessory); Settle();
        var before = view.FontSizeOffset;
        Wheel(window, accessory, 3, KeyModifiers.Control);
        Magnify(window, accessory, 0.5);
        Require(view.FontSizeOffset == before, "Gestures over a plugin accessory are not the terminal's.");
        view.Accessories.Children.Remove(accessory);

        view.ChangeFontSize(4 - view.FontSizeOffset);
        var reset = skia.Terminal.MenuExtras!().OfType<MenuItem>().Single(item => (string?)item.Header == "Reset Font Size");
        Require(reset.IsEnabled, "Reset Font Size is offered while the size differs from the base.");
        reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle();
        Require(view.FontSizeOffset == 0 && skia.Terminal.FontSize == baseSize && skia.Terminal.Size == grid, "Reset Font Size returns to the base size and the original grid.");
        Require(!skia.Terminal.MenuExtras!().OfType<MenuItem>().Single().IsEnabled, "Reset Font Size is disabled at the base size.");

        // Ghostty keeps the size per surface: a view rebuilt for the same terminal keeps it, a restart included.
        view.ChangeFontSize(3);
        view.Restart(); Until(() => view.Backend is SkiaTerminal { Started.IsCompletedSuccessfully: true });
        Require(((SkiaTerminal)view.Backend!).Terminal.FontSize == baseSize + 3, "A re-attach keeps the terminal's font size.");
        view.FocusTerminal(); Settle();
        var (rebuilt, rebuiltWindow) = Show(new Session(false), "font-a", yield: true);
        Require(rebuilt.FontSizeOffset == 3 && ((SkiaTerminal)rebuilt.Backend!).Terminal.FontSize == baseSize + 3, "A view rebuilt for the same terminal keeps its size.");
        Close(rebuilt, rebuiltWindow);

        // window-inherit-font-size: a terminal created here takes the focused terminal's size; a restored one does not.
        view.FocusTerminal(); Settle();
        var (created, createdWindow) = Show(new Session(false), "font-new", yield: false);
        Require(created.FontSizeOffset == 3, "A new terminal inherits the font size of the terminal focused last.");
        Close(created, createdWindow);
        view.FocusTerminal(); Settle();
        var (restored, restoredWindow) = Show(new Session(false), "font-restored", yield: true);
        Require(restored.FontSizeOffset == 0, "A restored terminal starts at the base size.");
        Close(restored, restoredWindow);
        view.ResetFontSize();
        Close(view, window);

        // A watched terminal is sized by its holder: a local font change redraws the cells but never resizes the PTY.
        var held = new Session(watching: true);
        var (watcher, watcherWindow) = Show(held, "font-watched", yield: true);
        var watched = (SkiaTerminal)watcher.Backend!;
        Require(watched.Watching, "The fixture watches a terminal another client holds.");
        var watchedGrid = watched.Terminal.Size;
        watcher.ChangeFontSize(4); Settle();
        Require(watched.Terminal.FontSize == baseSize + 4 && watched.Terminal.Size == watchedGrid && held.Resizes == 0,
            "A watcher's font size is its own: the holder's grid and PTY stay as they are.");
        watcher.ResetFontSize();
        Close(watcher, watcherWindow);
        Console.WriteLine("PASS terminal font zoom: wheel and pinch size the terminal's font and grid, reset, per-surface memory, inheritance, watchers");
    }

    internal static void Wheel(Window window, Control source, double delta, KeyModifiers modifiers) => source.RaiseEvent(new PointerWheelEventArgs(source,
        new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), window, default, 0, new PointerPointProperties(), modifiers, new Vector(0, delta))
    { RoutedEvent = InputElement.PointerWheelChangedEvent });

    internal static void Magnify(Window window, Control source, double delta) => source.RaiseEvent(new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent, source,
        new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), window, default, 0, new PointerPointProperties(), KeyModifiers.None, new Vector(delta, 0)));

    /// <summary>A Skia terminal on a fake session, for a check that hosts it in a window of its own.</summary>
    internal static TerminalView Create(string sessionId, bool yield = false) =>
        new(launch => new SkiaTerminal(launch, new Service(new Session(false))), new("/tmp", sessionId, "/tmp", "client") { Yield = yield });

    private static (TerminalView, Window) Show(Session session, string sessionId, bool yield)
    {
        var view = new TerminalView(launch => new SkiaTerminal(launch, new Service(session)), new("/tmp", sessionId, "/tmp", "client") { Yield = yield });
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        Until(() => view.Backend is { Started.IsCompletedSuccessfully: true } backend && backend.View.Bounds.Height > 0);
        Settle();
        return (view, window);
    }

    private static void Close(TerminalView view, Window window) { view.Dispose(); window.Close(); }

    private sealed class Service(Session session) : ITerminalService
    {
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<ITerminalSession>(session);
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Session(bool watching) : ITerminalSession
    {
        private readonly Channel<ReadOnlyMemory<byte>> output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private (int, int) lastResize;
        private int resizes;
        internal (int Columns, int Rows) LastResize { get { lock (output) return lastResize; } }
        internal int Resizes => Volatile.Read(ref resizes);
        public string Id => "font-fixture";
        public bool Created => true;
        public ReadOnlyMemory<byte> Replay => "prompt> "u8.ToArray();
        public long Position => 0;
        public bool Watching => watching;
        public (int Columns, int Rows) Grid => (60, 20);
        public TerminalPrefill? Prefill => null;
        public Task<int> Exit => new TaskCompletionSource<int>().Task;
        public Task Detached => new TaskCompletionSource().Task;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await foreach (var value in output.Reader.ReadAllAsync(cancellationToken)) yield return value; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            lock (output) lastResize = (columns, rows);
            Interlocked.Increment(ref resizes);
            return ValueTask.CompletedTask;
        }
        public ValueTask KillAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}