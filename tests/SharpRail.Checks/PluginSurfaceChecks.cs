using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Plugins.Isolation;
using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI;
using SharpRail.UI.Plugins;

namespace SharpRail.Checks;

/// <summary>
/// <c>--plugin-surface</c>: an external plugin's views rendered in its process and shown as pixels in the app
/// (src/SharpRail.Plugins.Api/Security.SPEC.md, UI halves). The tile codec and the host's relay on their own, then the
/// outsider fixture in a headless workbench: its side tool, settings section and file viewer show exactly what the
/// plugin's process renders, a click and a key press on its button change its settings and the view, typed text
/// reaches its note, Tab leaves the surface, a wheel scrolls its list with a move, a theme change re-renders it, and the
/// side tool hidden past 2 seconds and shown again opens a new surface.
/// </summary>
internal static class PluginSurfaceChecks
{
    private const string Id = "outsider-ui";
    private static readonly string[] Names = ["OutsiderClick", "OutsiderList", "OutsiderNote"];

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Codec();
        Console.WriteLine("PASS plugin surface codec: solid and compressed tiles decode to their pixels, moves shift the same way on both ends, and short, long or unknown tile data is refused");
        Relay();
        Console.WriteLine("PASS plugin surface relay: the host refuses a frame with a tile or move outside its surface, too many tiles, an unknown codec, a tile longer than its pixels need, bytes that do not match, an oversized surface, frames out of order, one sent before the client took the last, or null where a list or its item belongs, and names the surface of a frame it cannot read; after a keyframe request it drops older frames and takes the keyframe");
        if (PluginChannelChecks.Runner() is not { } runner)
        {
            Console.WriteLine("SKIP plugin surface: these checks run self-contained, and the framework-dependent runner needs a shared .NET runtime");
            return;
        }
        App(Path.Combine(root, "surface-app"), runner);
    }

    private static byte[] Pattern(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(seed);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            // Mostly flat with runs of noise, as text on a background is.
            var alpha = (byte)(random.Next(4) == 0 ? random.Next(256) : 255);
            pixels[index + 3] = alpha;
            for (var channel = 0; channel < 3; channel++) pixels[index + channel] = (byte)Math.Min(alpha, index / 4 % 7 == 0 ? random.Next(256) : 40);
        }
        return pixels;
    }

    private static void Codec()
    {
        const int width = 100, height = 70;
        var stride = width * 4;
        var source = Pattern(width, height, 1);
        foreach (var (x, y, w, h) in new[] { (0, 0, 64, 64), (64, 0, 36, 64), (10, 64, 64, 6) })
        {
            var output = new ArrayBufferWriter<byte>();
            var codec = TileCodec.Encode(source, stride, x, y, w, h, output);
            Require(codec == TileCodecKind.Qoi, "A tile of many colours is compressed.");
            var target = new byte[source.Length];
            TileCodec.Decode(codec, output.WrittenSpan, target, stride, x, y, w, h);
            for (var row = 0; row < h; row++)
                Require(target.AsSpan((y + row) * stride + x * 4, w * 4).SequenceEqual(source.AsSpan((y + row) * stride + x * 4, w * 4)), "A compressed tile decodes to its pixels.");
            Refused(() => TileCodec.Decode(codec, output.WrittenSpan[..(output.WrittenCount / 2)], new byte[source.Length], stride, x, y, w, h), "A truncated tile is refused.");
            Refused(() => TileCodec.Decode(codec, [.. output.WrittenSpan, 0xc0], new byte[source.Length], stride, x, y, w, h), "A tile with bytes past its pixels is refused.");
        }
        var solid = new byte[64 * 64 * 4];
        for (var index = 0; index < solid.Length; index += 4) (solid[index], solid[index + 1], solid[index + 2], solid[index + 3]) = (10, 20, 30, 255);
        var flat = new ArrayBufferWriter<byte>();
        Require(TileCodec.Encode(solid, 256, 0, 0, 64, 64, flat) == TileCodecKind.Solid && flat.WrittenCount == 4, "A tile of one colour is 4 bytes.");
        var filled = new byte[solid.Length];
        TileCodec.Decode(TileCodecKind.Solid, flat.WrittenSpan, filled, 256, 0, 0, 64, 64);
        Require(filled.SequenceEqual(solid), "A solid tile fills its pixels.");
        Refused(() => TileCodec.Decode((TileCodecKind)42, flat.WrittenSpan, filled, 256, 0, 0, 64, 64), "An unknown codec is refused.");
        Refused(() => TileCodec.Decode(TileCodecKind.Solid, [1, 2, 3], filled, 256, 0, 0, 64, 64), "A solid tile of other than 4 bytes is refused.");

        // A move shifts rows and columns in place, whichever way it goes, as a naive copy would.
        foreach (var (dx, dy) in new[] { (0, 7), (0, -7), (5, 0), (-5, 3) })
        {
            var pixels = Pattern(width, height, 2);
            var expected = (byte[])pixels.Clone();
            var move = new SurfaceMove(10, 5, 60, 50, dx, dy);
            for (var row = 0; row < move.Height; row++)
                for (var column = 0; column < move.Width; column++)
                {
                    int fromRow = row - dy, fromColumn = column - dx;
                    if (fromRow < 0 || fromRow >= move.Height || fromColumn < 0 || fromColumn >= move.Width) continue;
                    pixels.AsSpan(((move.Y + fromRow) * width + move.X + fromColumn) * 4, 4).CopyTo(expected.AsSpan(((move.Y + row) * width + move.X + column) * 4, 4));
                }
            SurfacePixels.Move(pixels, stride, move);
            Require(pixels.SequenceEqual(expected), $"A move by ({dx}, {dy}) shifts its region as a copy would.");
        }
    }

    private static void Refused(Action decode, string message)
    {
        try { decode(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Relay()
    {
        static SurfaceFrameHeader Frame(long sequence = 1, int width = 200, int height = 100, List<SurfaceMove>? moves = null, params SurfaceTile[] tiles) =>
            new(1, sequence, width, height, 1, 200, 100, true, moves ?? [], [.. tiles]);
        var state = new SurfaceFrameState();
        var good = Frame(tiles: [new(0, 0, 64, 64, TileCodecKind.Solid, 4), new(64, 0, 64, 64, TileCodecKind.Qoi, 10)]);
        Require(SurfaceFrames.Violation(good, 14, state) is null, "A frame that keeps to the caps passes.");
        Require(SurfaceFrames.Violation(Frame(tiles: new SurfaceTile(180, 0, 64, 64, TileCodecKind.Solid, 4)), 4, state) is not null, "A tile past the surface's edge is refused.");
        Require(SurfaceFrames.Violation(Frame(tiles: new SurfaceTile(0, 0, 300, 64, TileCodecKind.Solid, 4)), 4, state) is not null, "A tile wider than 256 pixels is refused.");
        Require(SurfaceFrames.Violation(Frame(tiles: new SurfaceTile(0, 0, 64, 64, (TileCodecKind)42, 4)), 4, state) is not null, "An unknown codec is refused.");
        Require(SurfaceFrames.Violation(Frame(tiles: new SurfaceTile(0, 0, 64, 64, TileCodecKind.Solid, 5)), 5, state) is not null, "A solid tile of other than 4 bytes is refused.");
        Require(SurfaceFrames.Violation(good, 20, state) is not null, "Bytes that do not match the tiles are refused.");
        Require(SurfaceFrames.Violation(Frame(tiles: [.. Enumerable.Repeat(new SurfaceTile(0, 0, 1, 1, TileCodecKind.Solid, 4), SurfaceLimits.MostTiles + 1)]), (SurfaceLimits.MostTiles + 1) * 4, state) is not null,
            "More than 4,096 tiles in one message are refused.");
        Require(SurfaceFrames.Violation(Frame(width: 9000), 0, state) is not null, "A surface wider than 8,192 pixels is refused.");
        Require(SurfaceFrames.Violation(Frame(sequence: 2), 0, state) is not null, "A frame out of order is refused.");
        Require(SurfaceFrames.Violation(Frame(moves: [new(0, 0, 50, 50, 0, 10)]), 0, state) is not null, "A move on the first frame of a size is refused.");
        var shown = new SurfaceFrameState { Sequence = 1, Width = 200, Height = 100 };
        Require(SurfaceFrames.Violation(Frame(sequence: 2, moves: [new(0, 0, 50, 50, 0, 10)]), 0, shown) is null, "A move inside a surface of the same size passes.");
        Require(SurfaceFrames.Violation(Frame(sequence: 2, moves: [new(180, 0, 50, 50, 0, 10)]), 0, shown) is not null, "A move outside the surface is refused.");
        shown.Unacknowledged = true;
        Require(SurfaceFrames.Violation(Frame(sequence: 2), 0, shown) is not null, "A frame before the client took the last is refused.");
        Require(SurfaceFrames.Violation(Frame(tiles: new SurfaceTile(0, 0, 2, 2, TileCodecKind.Qoi, 21)), 21, new()) is not null, "A tile longer than QOI writes for its pixels is refused.");

        // A client that lost what it held asks for a keyframe: frames sent before it are dropped, and the keyframe
        // may follow a frame the client never took.
        shown.KeyframePending = true;
        Require(shown.Stale(Frame(sequence: 2)), "A frame sent before the keyframe was asked for is dropped.");
        var keyframe = Frame(sequence: 3) with { Keyframe = true };
        Require(!shown.Stale(keyframe) && SurfaceFrames.Violation(keyframe, 0, shown) is null, "A keyframe passes after an unacknowledged frame.");
        Require(SurfaceFrames.Violation(keyframe with { Sequence = 1 }, 0, shown) is not null, "A keyframe older than the last frame is refused.");
        Require(SurfaceFrames.Violation(keyframe with { Moves = [new(0, 0, 50, 50, 0, 10)] }, 0, shown) is not null, "A keyframe moves no pixels.");
        shown.Accept(keyframe);
        Require(!shown.KeyframePending && shown.Unacknowledged && shown.Acknowledge(3) && !shown.Acknowledge(3), "A keyframe ends the wait and is acknowledged once.");

        // The plugin's process is not trusted to keep to the schema: null lists or null items are refused, never thrown on.
        const string start = "{\"Surface\":7,\"Sequence\":1,\"Width\":200,\"Height\":100,\"Scale\":1,\"DesiredWidth\":200,\"DesiredHeight\":100,\"Last\":true,";
        foreach (var (rest, what) in new[] { ("\"Moves\":null,\"Tiles\":[]}", "null moves"), ("\"Moves\":[],\"Tiles\":null}", "null tiles"),
            ("\"Moves\":[null],\"Tiles\":[]}", "a null move"), ("\"Moves\":[],\"Tiles\":[null]}", "a null tile") })
        {
            var (named, header, error) = UiSessionRelay.Read(start + rest);
            Require(header is null ? named == 7 && error is not null : SurfaceFrames.Violation(header, 0, new()) is not null, $"A frame with {what} is refused and names its surface.");
        }
        Require(SurfaceFrames.Violation(Frame() with { Moves = null! }, 0, new()) is not null && SurfaceFrames.Violation(Frame() with { Tiles = [null!] }, 0, new()) is not null,
            "The relay's check refuses null parts itself.");
        Require(UiSessionRelay.Read("[1]") is (null, null, not null), "A frame that names no surface is refused.");
    }

    private static void App(string root, PluginChannelChecks.RunnerLaunch runner)
    {
        var workspace = Path.Combine(root, "workspace");
        var profile = Path.Combine(root, "profile");
        PluginIsolatedUIChecks.Install(Path.Combine(profile, "plugins"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "sample.outsider"), "Text from an outsider file.");
        var seam = PluginIsolatedUIChecks.Seam(runner);
        seam = seam with { RunnerEnvironment = new Dictionary<string, string>(seam.RunnerEnvironment) { [SurfaceProtocol.DiagnosticsVariable] = "1" } };
        var original = Ui.Theme;
        using var app = new E2eWorkspace(workspace, profileRoot: profile, isolation: seam);
        try
        {
            var window = app.Window;
            var loader = app.Workbench.PluginLoader;
            E2eWorkspace.Until(() => loader.Registry.Roster.Any(entry => entry.Id == Id));
            var enabled = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(Id, true));
            E2eWorkspace.Until(() => enabled.IsCompleted && loader.Registry.Active.Contains(Id) && Clicks(app) is not null);

            window.Layout.RestoreTool("plugin:" + Id + ":board");
            var board = Surface(window, _ => true);
            var shown = Settle(board, "the side tool");
            Require(Opaque(shown) > 500, "The side tool shows what the plugin drew, not an empty frame.");
            Require(Opaque(shown) < shown.Width * shown.Height / 2, "The plugin's surface is transparent where it draws nothing, so the app's slot shows through.");
            Drawn(window, board, shown);
            Console.WriteLine($"PASS plugin surface side tool: {shown.Width}x{shown.Height} device pixels equal the plugin's own render and the app's window draws them, frame {board.Frames}");

            // A pointer click on the plugin's button, through the app's window: the plugin counts it in its settings and redraws.
            var before = shown.Pixels;
            Click(window, board, shown.Bounds["OutsiderClick"]);
            E2eWorkspace.Until(() => Clicks(app) == 1);
            shown = Settle(board, "the side tool after a click", changedFrom: before);
            Console.WriteLine("PASS plugin surface pointer: a click on the plugin's button reaches it, its settings count 1 click, and the view shows the new count");

            // Focus enters with Tab at the plugin's first focusable element (its button); Enter clicks it.
            board.Focus(NavigationMethod.Tab);
            E2eWorkspace.Until(() => board.IsFocused);
            before = shown.Pixels;
            Press(window, Key.Enter, PhysicalKey.Enter);
            E2eWorkspace.Until(() => Clicks(app) == 2);
            shown = Settle(board, "the side tool after Enter", changedFrom: before);
            Console.WriteLine("PASS plugin surface keys: focus entering with Tab lands on the plugin's button and Enter clicks it");

            // Tab moves on to the note, typed text shows in it, and Tab past the note leaves the surface.
            Press(window, Key.Tab, PhysicalKey.Tab);
            before = shown.Pixels;
            window.KeyTextInput("hello");
            shown = Settle(board, "the side tool after typing", changedFrom: before);
            Press(window, Key.Tab, PhysicalKey.Tab);
            E2eWorkspace.Until(() => !board.IsFocused);
            Console.WriteLine("PASS plugin surface text and focus: Tab reaches the note, typed text shows in it, and Tab past the plugin's last element moves focus out of the surface");

            // A wheel over the list scrolls it; the frame carries a move, so only the uncovered rows travel as tiles.
            var moves = board.Moves;
            before = shown.Pixels;
            var list = shown.Bounds["OutsiderList"];
            window.MouseWheel(Point(window, board, list), new Vector(0, -1));
            E2eWorkspace.Until(() => board.Moves > moves);
            shown = Settle(board, "the side tool after scrolling", changedFrom: before);
            static int Span(double start, double length) => (int)Math.Floor((start + length - 1) / SurfaceLimits.TileSide) - (int)Math.Floor(start / SurfaceLimits.TileSide) + 1;
            Require(board.MovedFrameTiles < Span(list[0], list[2]) * Span(list[1], list[3]), "A scrolled frame sends fewer tiles than the list covers: the rest moves.");
            Console.WriteLine($"PASS plugin surface scroll: a wheel over the list sends a move, the moved frame carried {board.MovedFrameTiles} tiles ({board.MovedFrameBytes} bytes) for a list of {list[2]:0}x{list[3]:0}, and the pixels still equal the plugin's render");

            // A theme change re-renders the plugin's view under the new kit tokens.
            var other = SharpRail.UI.Rendering.Themes.All.First(theme => theme.IsLight != original.IsLight);
            before = shown.Pixels;
            Ui.Apply(other);
            shown = Settle(board, "the side tool after a theme change", changedFrom: before);
            Ui.Apply(original);
            Settle(board, "the side tool back on its theme");
            Console.WriteLine("PASS plugin surface theme: the app's theme change reaches the plugin's process and its view re-renders under it");

            window.ShowSettings("Plugins");
            E2eWorkspace.Until(() => window.OpenSettingsViews.Any());
            var settings = window.OpenSettingsViews.Single();
            settings.ShowSection("plugin:" + Id + ":" + Id);
            var section = Surface(settings, surface => surface != board);
            var sectionShown = Settle(section, "the settings section");
            Require(Opaque(sectionShown) > 200 && section.Bounds.Height > 20, "The settings section takes the height the plugin asks for and shows what it drew.");
            Console.WriteLine($"PASS plugin surface settings section: sized to the {section.Bounds.Height:0} pixels the plugin asks for, its pixels equal the plugin's render");
            settings.Close();

            app.ShowTool("files");
            app.Open("sample.outsider");
            var viewer = Surface(window, surface => surface != board && surface != section);
            var viewerShown = Settle(viewer, "the file viewer");
            Require(Opaque(viewerShown) > 100, "The file viewer shows what the plugin drew.");
            Console.WriteLine("PASS plugin surface file viewer: a .outsider file opens in the plugin's viewer, rendered in its process");

            // The workbench keeps the side tool's control while another tool shows: past 2 seconds detached its surface
            // closes, and shown again the same control opens a new one and shows the plugin's pixels.
            var hidden = board.Surface;
            Require(TopLevel.GetTopLevel(board) is null, "Showing the files tool hides the plugin's side tool.");
            var away = Stopwatch.StartNew();
            E2eWorkspace.Until(() => away.Elapsed > TimeSpan.FromSeconds(2.5));
            E2eWorkspace.Until(() => hidden.Events.Completion.IsCompleted);
            app.ShowTool("plugin:" + Id + ":board");
            E2eWorkspace.Until(() => TopLevel.GetTopLevel(board) is not null && !ReferenceEquals(board.Surface, hidden));
            var again = Settle(board, "the side tool shown again");
            Require(Opaque(again) > 500 && board.Child is null, "The side tool shown again shows what the plugin drew, not a message.");
            Console.WriteLine("PASS plugin surface reattach: hidden past 2 seconds the side tool's surface closes, and shown again it opens a new one whose pixels equal the plugin's render");
        }
        finally
        {
            if (!ReferenceEquals(Ui.Theme, original)) Ui.Apply(original);
        }
    }

    internal static int? Clicks(E2eWorkspace app) =>
        app.Workbench.State.Current.PluginSettings.TryGetValue(Id, out var space) && space.ValueKind == JsonValueKind.Object && space.TryGetProperty("clicks", out var clicks) ? clicks.GetInt32() : space.ValueKind == JsonValueKind.Object ? 0 : null;

    internal static RemoteSurface Surface(Visual within, Func<RemoteSurface, bool> fits)
    {
        RemoteSurface? found = null;
        E2eWorkspace.Until(() => (found = within.GetVisualDescendants().OfType<RemoteSurface>().FirstOrDefault(fits)) is { Frames: > 0 });
        return found!;
    }

    internal sealed record Shown(int Width, int Height, byte[] Pixels, Dictionary<string, double[]> Bounds);

    /// <summary>
    /// Waits until the pixels the app decoded equal a fresh render in the plugin's process (and differ from
    /// <paramref name="changedFrom"/> when given): lossless tiles leave no difference.
    /// </summary>
    internal static Shown Settle(RemoteSurface surface, string what, byte[]? changedFrom = null, Func<RemoteSurface, UiSessionRelay.RelaySurface?>? relay = null)
    {
        var clock = Stopwatch.StartNew();
        string last = "no capture";
        while (clock.Elapsed < PluginChannelChecks.Patience)
        {
            // A remote client's surface reaches the host's relay by the number the host gave it.
            if ((relay?.Invoke(surface) ?? surface.Surface as UiSessionRelay.RelaySurface) is not { } held)
            {
                last = "the host has not numbered the surface yet";
                E2eWorkspace.Settle(50);
                continue;
            }
            var capture = held.CaptureAsync(Names);
            E2eWorkspace.Until(() => capture.IsCompleted);
            var reference = capture.GetAwaiter().GetResult();
            var (width, height, pixels) = surface.Snapshot();
            var expected = Convert.FromBase64String(reference.Pixels);
            if (width == reference.Width && height == reference.Height && pixels.AsSpan().SequenceEqual(expected) && (changedFrom is null || !pixels.AsSpan().SequenceEqual(changedFrom)))
                return new(width, height, pixels, reference.Bounds);
            last = $"app {width}x{height}, plugin {reference.Width}x{reference.Height}, differing bytes {(pixels.Length == expected.Length ? pixels.Where((value, index) => value != expected[index]).Count() : -1)}";
            var wait = Stopwatch.StartNew();
            E2eWorkspace.Until(() => wait.ElapsedMilliseconds > 50);
        }
        throw new InvalidOperationException($"The app's pixels for {what} never matched the plugin's render: {last}.");
    }

    /// <summary>The app's own rendered frame shows the surface's opaque pixels where the surface sits.</summary>
    private static void Drawn(TopLevel window, RemoteSurface surface, Shown shown)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The app's window rendered nothing.");
        using var locked = frame.Lock();
        var scale = window.RenderScaling;
        var origin = surface.TranslatePoint(default, window) ?? throw new InvalidOperationException("The surface is not in the window.");
        var (left, top) = ((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale));
        var swap = locked.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        int compared = 0, wrong = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
        var sample = "";
        for (var y = 0; y < shown.Height; y++)
            for (var x = 0; x < shown.Width; x++)
            {
                var at = (y * shown.Width + x) * 4;
                // The dock's own edges may draw over the slot's outermost pixels.
                if (shown.Pixels[at + 3] != 255 || x < 2 || y < 2 || x >= shown.Width - 2 || y >= shown.Height - 2 || left + x >= locked.Size.Width || top + y >= locked.Size.Height) continue;
                var pixel = new byte[4];
                System.Runtime.InteropServices.Marshal.Copy(locked.Address + (top + y) * locked.RowBytes + (left + x) * 4, pixel, 0, 4);
                if (swap) (pixel[0], pixel[2]) = (pixel[2], pixel[0]);
                compared++;
                for (var channel = 0; channel < 3; channel++)
                    if (Math.Abs(pixel[channel] - shown.Pixels[at + channel]) > 1)
                    {
                        wrong++;
                        (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                        if (sample.Length < 300) sample += $" ({x},{y}) app {pixel[0]},{pixel[1]},{pixel[2]} plugin {shown.Pixels[at]},{shown.Pixels[at + 1]},{shown.Pixels[at + 2]};";
                        break;
                    }
            }
        Require(compared > 300 && wrong == 0, $"The app's window shows the plugin's opaque pixels where its surface sits: {wrong} of {compared} differ in ({minX},{minY})-({maxX},{maxY}), origin {left},{top}:{sample}");
    }

    internal static int Opaque(Shown shown)
    {
        var count = 0;
        for (var index = 3; index < shown.Pixels.Length; index += 4) if (shown.Pixels[index] != 0) count++;
        return count;
    }

    internal static Point Point(TopLevel window, RemoteSurface surface, double[] bounds) =>
        surface.TranslatePoint(new Point(bounds[0] + bounds[2] / 2, bounds[1] + bounds[3] / 2), window) ?? throw new InvalidOperationException("The surface is not in the window.");

    internal static void Click(TopLevel window, RemoteSurface surface, double[] bounds)
    {
        var at = Point(window, surface, bounds);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    internal static void Press(TopLevel window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
    }
}