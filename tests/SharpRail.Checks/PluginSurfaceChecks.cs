using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Plugins.Isolation;
using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.Plugins;

namespace SharpRail.Checks;

/// <summary>
/// <c>--plugin-surface</c>: an external plugin's views rendered in its process and shown as pixels in the app
/// (src/SharpRail.Plugins.Api/Security.SPEC.md, UI halves). The tile codec and the host's relay on their own, then the
/// outsider fixture in a headless workbench: its side tool, settings section and file viewer show exactly what the
/// plugin's process renders, a click and a key press on its button change its settings and the view, typed text
/// reaches its note, Tab leaves the surface, a wheel scrolls its list with a move, a theme change re-renders it, its
/// context menu, flyout and tooltip show as popups of their own at the right place and under the app's dialogs, a click
/// in a popup reaches the plugin, a press elsewhere and Escape close them, scale 2 renders twice the pixels, and the
/// side tool hidden past 2 seconds and shown again opens a new surface. A plugin's process that breaks the popup rules
/// is held to them by the app on its own. The plugin's cursor shows over its link, the app's input method composes into
/// its text field, and its dialog shows framed in the app, modal to the plugin only, closing with and without a result.
/// </summary>
internal static class PluginSurfaceChecks
{
    private const string Id = "outsider-ui";
    private static readonly string[] Names = ["OutsiderClick", "OutsiderList", "OutsiderNote", "OutsiderFlood", "OutsiderMenuTarget", "OutsiderMore", "OutsiderTip",
        "OutsiderMenuCount", "OutsiderFlyoutCount", "OutsiderLink", "OutsiderIme", "OutsiderAsk", "OutsiderDialogYes"];

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
        PopupStates();
        Console.WriteLine($"PASS plugin surface popup caps: the host refuses a popup numbered below 1 or again after it closed, placed or sized out of range, closed when not open, more than {SurfaceLimits.MostPopups} open, and a frame for a popup that is not open; each popup keeps its own frames, and one opened while a keyframe is awaited awaits one too; a title on a popup, a dialog title too long or with control characters, a dialog turned popup, a cursor shape out of range and a caret out of range or on a popup not open are refused");
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

    private static void PopupStates()
    {
        static SurfacePopup Popup(int number, bool open = true, double x = 10, double y = 20, double width = 120, double height = 80) => new(1, number, x, y, width, height, open);
        static SurfaceFrameHeader Frame(int popup, long sequence = 1, bool keyframe = true) =>
            new(1, sequence, 120, 80, 1, 0, 0, true, [], [new(0, 0, 64, 64, TileCodecKind.Solid, 4)], keyframe, popup);
        var state = new SurfaceState();
        Require(state.Violation(Frame(1), 4) is not null, "A frame for a popup that is not open is refused.");
        Require(state.Place(Popup(0)) is not null, "A popup numbered below 1 is refused.");
        Require(state.Place(Popup(1, x: double.NaN)) is not null && state.Place(Popup(1, x: 9000)) is not null && state.Place(Popup(1, width: 0)) is not null &&
            state.Place(Popup(1, height: 9000)) is not null, "A popup placed or sized out of range is refused.");
        Require(state.Place(Popup(1, open: false)) is not null, "Closing a popup that is not open is refused.");
        Require(state.Place(Popup(1)) is null && state.Place(Popup(1, x: -40)) is null, "A popup opens and moves, even above or left of its surface.");
        Require(state.Violation(Frame(1), 4) is null, "A popup's first frame passes.");
        state.Frames(1)!.Accept(Frame(1));
        Require(state.Frames(0)!.Sequence == 0 && state.Violation(Frame(0, keyframe: false), 4) is null, "The surface's frames are numbered apart from its popups'.");
        Require(state.Frames(1)!.Acknowledge(1), "A popup's frame is acknowledged on its own.");
        Require(state.Place(Popup(1, open: false)) is null && state.Frames(1) is null && state.Violation(Frame(1, 2), 4) is not null, "A closed popup takes no more frames.");
        Require(state.Place(Popup(1)) is not null, "A popup's number is not used again.");
        for (var number = 2; number < 2 + SurfaceLimits.MostPopups; number++) Require(state.Place(Popup(number)) is null, "Popups open up to the cap.");
        Require(state.Place(Popup(2 + SurfaceLimits.MostPopups)) is not null, "One popup past the cap is refused.");
        var waiting = new SurfaceState();
        waiting.AwaitKeyframe(lost: false);
        Require(waiting.Place(Popup(3)) is null && waiting.Frames(3)!.KeyframePending && waiting.Stale(Frame(3, keyframe: false)) && !waiting.Stale(Frame(3)),
            "A popup opened while a keyframe is awaited drops frames until its own keyframe.");

        var reports = new SurfaceState();
        Require(reports.Place(new SurfacePopup(1, 1, 0, 0, 120, 80, true, Title: "A title")) is not null, "A popup that is not a dialog takes no title.");
        Require(reports.Place(new SurfacePopup(1, 1, 0, 0, 120, 80, true, true, true, new string('t', SurfaceLimits.MostTitle + 1))) is not null &&
            reports.Place(new SurfacePopup(1, 1, 0, 0, 120, 80, true, true, true, "A\ntitle")) is not null, "A dialog's title past the cap or with control characters is refused.");
        Require(reports.Place(new SurfacePopup(1, 1, 0, 0, 120, 80, true, true, true, "A title")) is null, "A dialog opens with a plain title.");
        Require(reports.Place(new SurfacePopup(1, 1, 0, 0, 120, 80, true)) is not null, "A dialog does not turn into a popup.");
        Require(reports.Cursor(new(1, 0, 9)) is null && reports.Cursor(new(1, 1, 0)) is null, "A known cursor shape over the surface or an open popup passes.");
        Require(reports.Cursor(new(1, 0, -1)) is not null && reports.Cursor(new(1, 0, SurfaceLimits.MostCursorShape + 1)) is not null && reports.Cursor(new(1, 2, 0)) is not null,
            "A cursor shape out of range, or on a popup that is not open, is refused.");
        Require(reports.Ime(new(1, 0, true, 10, 20, 1, 16)) is null && reports.Ime(new(1, 0, false, 0, 0, 0, 0)) is null, "A caret inside range passes.");
        Require(reports.Ime(new(1, 0, true, double.NaN, 0, 1, 16)) is not null && reports.Ime(new(1, 0, true, 0, 0, -1, 16)) is not null &&
            reports.Ime(new(1, 0, true, 0, 0, 1, 9000)) is not null && reports.Ime(new(1, 3, true, 0, 0, 1, 16)) is not null,
            "A caret out of range, or on a popup that is not open, is refused.");
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

            shown = Popups(app, window, board, Settle(board, "the side tool before its popups"), null, "plugin surface");
            shown = Extras(app, window, board, shown, null, "plugin surface");
            Covered(window, board, shown);
            Console.WriteLine("PASS plugin surface popups under dialogs: the popup layer covers only the workbench's row, below the title bar; with the plugin's menu open, the app's dialog opens in a layer above the popup layer in the same panel, and focus moving into it closes the menu");
            Hostile();
            Console.WriteLine("PASS plugin surface hostile popups: with a plugin's process that ignores dismissal, a popup that opens with no input to the surface does not show, one after input stays inside the cell of the surface's holder, below a title row, a press elsewhere hides it and it never shows again, Escape hides the newest popup that takes input, a tooltip does not keep Escape from the app, and focus leaving hides the rest; a dialog larger than the window keeps its close button in view, which removes it though the plugin keeps it open, and it never shows again");
            shown = Scaled(window, board, shown, null, "plugin surface");

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

    /// <summary>
    /// The board's popups through the app's window: the context menu opens at a right-click as its own surface with the
    /// plugin's pixels and a click on its item counts; the flyout opens below its button and a press elsewhere in the app
    /// closes it without counting, then a click in it counts and Escape closes it; the tooltip opens under the pointer,
    /// lets the pointer through and closes as the pointer leaves.
    /// </summary>
    internal static Shown Popups(E2eWorkspace app, TopLevel window, RemoteSurface board, Shown shown, Func<RemoteSurface, UiSessionRelay.RelaySurface?>? relay, string what)
    {
        var clicks = Clicks(app);
        var at = Point(window, board, shown.Bounds["OutsiderMenuTarget"]);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        var menu = Popup(board);
        var corner = menu.TranslatePoint(default, window) ?? throw new InvalidOperationException("The menu is not in the window.");
        Require(Math.Abs(corner.X - at.X) <= 8 && Math.Abs(corner.Y - at.Y) <= 8, $"The context menu opens at the right-click {at}, not at {corner}.");
        Require(menu.Parent is Canvas && !board.IsVisualAncestorOf(menu), "The context menu shows in the popup layer, not inside the surface.");
        var menuShown = Settle(board, "the context menu", relay: relay, popup: menu.Number);
        Drawn(window, menu, menuShown);
        Click(window, menu, menuShown.Bounds["OutsiderMenuCount"]);
        E2eWorkspace.Until(() => Clicks(app) == clicks + 1 && board.Popups.Count == 0);
        Console.WriteLine($"PASS {what} context menu: a right-click opens the plugin's menu as its own {menuShown.Width}x{menuShown.Height} surface at the pointer, the app draws the plugin's pixels there, and a click on its item reaches the plugin and closes it");

        var more = shown.Bounds["OutsiderMore"];
        Click(window, board, more);
        var flyout = Popup(board);
        var below = board.TranslatePoint(new Point(more[0], more[1] + more[3]), window) ?? throw new InvalidOperationException("The surface is not in the window.");
        var top = flyout.TranslatePoint(default, window) ?? throw new InvalidOperationException("The flyout is not in the window.");
        Require(Math.Abs(top.Y - below.Y) <= 12 && top.X <= below.X + more[2] && top.X + flyout.Bounds.Width >= below.X,
            $"The flyout opens below its button at {below}, not at {top}.");
        var away = new Point(3, 3);
        Require(window.InputHitTest(away) is not Visual pressed || !(pressed == board || board.IsVisualAncestorOf(pressed) || pressed == flyout || flyout.IsVisualAncestorOf(pressed)),
            "The press elsewhere lands outside the surface and its popups.");
        window.MouseDown(away, MouseButton.Left);
        window.MouseUp(away, MouseButton.Left);
        E2eWorkspace.Until(() => board.Popups.Count == 0);
        Require(Clicks(app) == clicks + 1, "A press elsewhere closes the flyout without clicking in it.");
        // A press on the plugin's own surface outside the popup closes it in the plugin's process.
        Click(window, board, more);
        Popup(board);
        Click(window, board, shown.Bounds["OutsiderMenuTarget"]);
        E2eWorkspace.Until(() => board.Popups.Count == 0);
        Require(Clicks(app) == clicks + 1, "A press on the surface outside the flyout closes it without clicking in it.");
        Click(window, board, more);
        flyout = Popup(board);
        var flyoutShown = Settle(board, "the flyout", relay: relay, popup: flyout.Number);
        Click(window, flyout, flyoutShown.Bounds["OutsiderFlyoutCount"]);
        E2eWorkspace.Until(() => Clicks(app) == clicks + 2);
        board.Focus(NavigationMethod.Pointer);
        E2eWorkspace.Until(() => board.IsFocused);
        Press(window, Key.Escape, PhysicalKey.Escape);
        E2eWorkspace.Until(() => board.Popups.Count == 0);
        Console.WriteLine($"PASS {what} flyout: the plugin's flyout opens below its button, a press elsewhere in the app or on the surface outside it closes it without a click, a click in it reaches the plugin, and Escape closes it");

        window.MouseMove(Point(window, board, shown.Bounds["OutsiderTip"]));
        var tip = Popup(board);
        Require(!tip.IsHitTestVisible, "The tooltip lets the pointer through.");
        Settle(board, "the tooltip", relay: relay, popup: tip.Number);
        window.MouseMove(away);
        E2eWorkspace.Until(() => board.Popups.Count == 0);
        Console.WriteLine($"PASS {what} tooltip: hovering opens the plugin's tooltip as a popup that lets the pointer through, and it closes as the pointer leaves");
        return Settle(board, "the side tool after its popups", relay: relay);
    }

    /// <summary>
    /// The board's extras through the app's window: the hand cursor over its link and the arrow off it; its text field
    /// composing with the app's input method (offered only while that field has focus, at its caret) and taking the
    /// committed text; its dialog framed and named in the app, holding the plugin's views while open, closed by the
    /// frame's button and by Escape without a result, and by its own button with one, which the plugin counts.
    /// </summary>
    internal static Shown Extras(E2eWorkspace app, TopLevel window, RemoteSurface board, Shown shown, Func<RemoteSurface, UiSessionRelay.RelaySurface?>? relay, string what)
    {
        window.MouseMove(Point(window, board, shown.Bounds["OutsiderLink"]));
        E2eWorkspace.Until(() => board.CursorShape == StandardCursorType.Hand && board.Cursor is not null);
        window.MouseMove(Point(window, board, shown.Bounds["OutsiderClick"]));
        E2eWorkspace.Until(() => board.CursorShape == StandardCursorType.Arrow);
        Console.WriteLine($"PASS {what} cursor: the plugin's hand cursor over its link shows over the surface in the app, and the arrow again off it");

        board.Focus(NavigationMethod.Pointer);
        E2eWorkspace.Until(() => board.IsFocused);
        Require(Offered(board) is null, "The app's input method composes for the plugin only while one of its text fields has focus.");
        var field = shown.Bounds["OutsiderIme"];
        Click(window, board, field);
        E2eWorkspace.Until(() => Offered(board) is { } offered && offered.CursorRectangle is { Height: > 0 } caret &&
            caret.X >= field[0] - 1 && caret.Right <= field[0] + field[2] + 1 && caret.Y >= field[1] - 1 && caret.Bottom <= field[1] + field[3] + 1);
        var client = Offered(board)!;
        client.SetPreeditText("にほ", 2);
        Captured(board, relay, texts => texts["OutsiderIme.preedit"] == "にほ" && texts["OutsiderIme"] == "", "the composition");
        client.SetPreeditText(null);
        window.KeyTextInput("日本");
        Captured(board, relay, texts => texts["OutsiderIme.preedit"] == "" && texts["OutsiderIme"] == "日本", "the committed text");
        shown = Settle(board, "the side tool after composing", relay: relay);
        Console.WriteLine($"PASS {what} input method: with the plugin's text field focused the app's input method composes at its caret, the plugin's field shows the composition and then takes the committed text, and with none focused the app offers no composition");

        var clicks = Clicks(app);
        RemoteSurface.RemotePopup Dialog()
        {
            Click(window, board, shown.Bounds["OutsiderAsk"]);
            RemoteSurface.RemotePopup? dialog = null;
            E2eWorkspace.Until(() => (dialog = board.Popups.FirstOrDefault(popup => popup.Dialog)) is { IsFocused: true });
            return dialog!;
        }
        var dialog = Dialog();
        Require(dialog.Title.EndsWith(": Count a click?", StringComparison.Ordinal) && dialog.Title.Length > ": Count a click?".Length,
            $"The dialog's frame names the plugin and the dialog, not \"{dialog.Title}\".");
        Require(dialog.Shown.Name == "PluginDialogFrame" && dialog.Shown.Parent is Canvas, "The dialog shows inside the app's frame, in the plugin's layer.");
        var dialogShown = Settle(board, "the dialog", relay: relay, popup: dialog.Number);
        Drawn(window, dialog, dialogShown);
        // The plugin's own view waits for its dialog; the press reaches the plugin and does nothing.
        Click(window, board, shown.Bounds["OutsiderClick"]);
        var wait = Stopwatch.StartNew();
        E2eWorkspace.Until(() => wait.ElapsedMilliseconds > 400);
        Require(Clicks(app) == clicks && board.Popups.Contains(dialog), "A click on the plugin's view while its dialog is open does nothing, and the dialog stays.");
        var close = dialog.Shown.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PluginDialogClose");
        var closeAt = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window) ?? throw new InvalidOperationException("The close button is not in the window.");
        window.MouseMove(closeAt);
        window.MouseDown(closeAt, MouseButton.Left);
        window.MouseUp(closeAt, MouseButton.Left);
        E2eWorkspace.Until(() => !board.Popups.Any(popup => popup.Dialog));
        Require(Clicks(app) == clicks, "The frame's close button closes the dialog without a result.");

        dialog = Dialog();
        Press(window, Key.Escape, PhysicalKey.Escape);
        E2eWorkspace.Until(() => !board.Popups.Any(popup => popup.Dialog));
        Require(Clicks(app) == clicks, "Escape in the dialog closes it without a result.");

        dialog = Dialog();
        dialogShown = Settle(board, "the dialog again", relay: relay, popup: dialog.Number);
        Click(window, dialog, dialogShown.Bounds["OutsiderDialogYes"]);
        E2eWorkspace.Until(() => !board.Popups.Any(popup => popup.Dialog) && Clicks(app) == clicks + 1);
        Console.WriteLine($"PASS {what} dialog: the plugin's dialog opens framed with the plugin's name and its title, its pixels drawn in the app, the plugin's view waits while it is open, the frame's close button and Escape close it with no result, and its own button closes it with the result the plugin counts");
        return Settle(board, "the side tool after its dialog", relay: relay);
    }

    // The input method client the app's input method would compose into on the surface, if any.
    private static Avalonia.Input.TextInput.TextInputMethodClient? Offered(RemoteSurface board)
    {
        var request = new Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        board.RaiseEvent(request);
        return request.Client;
    }

    // Captures the surface in the plugin's process until its text fields read as <paramref name="expected"/> says.
    private static Dictionary<string, string> Captured(RemoteSurface surface, Func<RemoteSurface, UiSessionRelay.RelaySurface?>? relay, Func<Dictionary<string, string>, bool> expected, string what)
    {
        var clock = Stopwatch.StartNew();
        var last = new Dictionary<string, string>();
        while (clock.Elapsed < PluginChannelChecks.Patience)
        {
            if ((relay?.Invoke(surface) ?? surface.Surface as UiSessionRelay.RelaySurface) is { } held)
            {
                var capture = held.CaptureAsync(Names);
                E2eWorkspace.Until(() => capture.IsCompleted);
                last = capture.GetAwaiter().GetResult().Texts ?? [];
                if (last.ContainsKey("OutsiderIme") && expected(last)) return last;
            }
            var wait = Stopwatch.StartNew();
            E2eWorkspace.Until(() => wait.ElapsedMilliseconds > 50);
        }
        throw new InvalidOperationException($"The plugin's text field never showed {what}: {string.Join(", ", last.Select(pair => pair.Key + "=" + pair.Value))}.");
    }

    /// <summary>
    /// The app's dialog opened over an open plugin menu lies above the menu's layer in the same panel and takes the
    /// pointer where the menu is, and focus moving into the dialog closes the menu.
    /// </summary>
    private static void Covered(Window window, RemoteSurface board, Shown shown)
    {
        var at = Point(window, board, shown.Bounds["OutsiderMenuTarget"]);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        var menu = Popup(board);
        var middle = menu.TranslatePoint(new Point(menu.Bounds.Width / 2, menu.Bounds.Height / 2), window) ?? throw new InvalidOperationException("The menu is not in the window.");
        var layer = (Canvas)menu.Parent!;
        var title = window.GetVisualDescendants().OfType<Control>().First(control => control.Name == "WindowTitleBar");
        var below = title.TranslatePoint(new Point(0, title.Bounds.Height), window)!.Value.Y;
        Require(layer.ClipToBounds && layer.TranslatePoint(default, window)!.Value.Y >= below - 0.5,
            $"The plugin's popup layer starts below the title bar at {below:0}, clipped to its cell.");
        // Focus moving into the dialog hides the menu and its layer at once, so the layer's place is taken first.
        var panel = (Panel)layer.Parent!;
        var notice = Dialogs.Notice(window, "Above the plugin", "The app's dialogs stay above a plugin's popups.");
        E2eWorkspace.Until(() => DialogWindow.On(window).Count == 1);
        var dialog = ((DialogWindow)DialogWindow.On(window).Single()).Surface!.GetVisualAncestors().OfType<OverlayHost>().First();
        Require(dialog.Parent == panel && (dialog.ZIndex > layer.ZIndex || dialog.ZIndex == layer.ZIndex && panel.Children.IndexOf(dialog) > panel.Children.IndexOf(layer)),
            $"The app's dialog layer ({dialog.ZIndex}) lies above the plugin's popup layer ({layer.ZIndex}) in the same panel.");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var hit = window.InputHitTest(middle) as Visual;
        Require(board.Popups.Count == 0 || hit is not null && hit != menu && !menu.IsVisualAncestorOf(hit) && hit.FindAncestorOfType<OverlayHost>(includeSelf: true) is not null,
            $"The app's dialog takes the pointer where the plugin's menu is, not {hit?.GetType().Name} {(hit as Control)?.Name}.");
        E2eWorkspace.Until(() => board.Popups.Count == 0);
        ((DialogWindow)DialogWindow.On(window).Single()).Close();
        E2eWorkspace.Until(() => notice.IsCompleted);
    }

    /// <summary>
    /// The app's window at scale 2: the surface keeps its size in the app and renders at twice the pixels, which equal a
    /// fresh render in the plugin's process and show in the window, and so does a popup; back at scale 1 it renders as before.
    /// </summary>
    internal static Shown Scaled(TopLevel window, RemoteSurface board, Shown shown, Func<RemoteSurface, UiSessionRelay.RelaySurface?>? relay, string what, Action? atScale2 = null)
    {
        var size = board.Bounds.Size;
        window.SetRenderScaling(2);
        E2eWorkspace.Until(() => board.FrameScale == 2);
        var scaled = Settle(board, "the side tool at scale 2", relay: relay);
        Require(Math.Abs(scaled.Width - 2 * shown.Width) <= 2 && Math.Abs(scaled.Height - 2 * shown.Height) <= 2,
            $"At scale 2 the surface renders twice the pixels: {scaled.Width}x{scaled.Height} for {shown.Width}x{shown.Height}.");
        Require(Math.Abs(board.Bounds.Width - size.Width) <= 1 && Math.Abs(board.Bounds.Height - size.Height) <= 1, "The surface keeps its size in the app at scale 2.");
        Drawn(window, board, scaled);
        atScale2?.Invoke();
        var at = Point(window, board, scaled.Bounds["OutsiderMenuTarget"]);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        var menu = Popup(board);
        var menuShown = Settle(board, "the context menu at scale 2", relay: relay, popup: menu.Number);
        Require(Math.Abs(menuShown.Width - 2 * menu.Bounds.Width) <= 2, $"A popup at scale 2 renders twice the pixels: {menuShown.Width} for {menu.Bounds.Width:0}.");
        Drawn(window, menu, menuShown);
        Press(window, Key.Escape, PhysicalKey.Escape);
        E2eWorkspace.Until(() => board.Popups.Count == 0);
        window.SetRenderScaling(1);
        E2eWorkspace.Until(() => board.FrameScale == 1);
        var back = Settle(board, "the side tool back at scale 1", relay: relay);
        Require(back.Width == shown.Width && back.Height == shown.Height, "Back at scale 1 the surface renders its first size again.");
        Console.WriteLine($"PASS {what} scale: at scale 2 the side tool renders {scaled.Width}x{scaled.Height} device pixels for {shown.Width}x{shown.Height} at scale 1, equal to the plugin's render and drawn in the window, a popup renders at twice its pixels too, and back at scale 1 it renders as before");
        return back;
    }

    /// <summary>
    /// A plugin's process that breaks every popup rule, on a surface in a window of its own whose grid has a title row
    /// above the surface's row: it opens popups unprompted and as large as the relay allows, ignores dismissal and sends
    /// a hidden popup's place again.
    /// </summary>
    private static void Hostile()
    {
        var hostile = new HostileSurface();
        var surface = new RemoteSurface(() => hostile) { Width = 200, Height = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var title = new Button { Name = "HostileTitle", Content = "Title", Width = 100, Height = 40 };
        var holder = new Border { Child = surface };
        Grid.SetRow(holder, 1);
        var overlays = new OverlayHost();
        Grid.SetRowSpan(overlays, 2);
        var window = new Window { Width = 400, Height = 400, Content = new Grid { RowDefinitions = new("40,*"), Children = { title, holder, overlays } } };
        var heard = false;
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => heard = !e.Handled, RoutingStrategies.Bubble, handledEventsToo: true);
        window.Show();
        try
        {
            var sequence = 0L;
            void Sync()
            {
                var frames = surface.Frames;
                hostile.Frame(++sequence);
                E2eWorkspace.Until(() => surface.Frames > frames);
            }
            Sync();
            hostile.Open(1);
            Sync();
            Require(surface.Popups.Count == 0, "A popup that opens with no input to the surface does not show.");

            window.MouseMove(new Point(100, 140));
            hostile.Open(2);
            Sync();
            Require(surface.Popups.Count == 1, "A popup that opens just after input shows.");
            var popup = surface.Popups.Single();
            var layer = (Canvas)popup.Parent!;
            Require(Grid.GetRow(layer) == 1 && Grid.GetRowSpan(layer) == 1 && layer.ClipToBounds, "The popup layer takes the cell of the surface's holder, clipped to it.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Require(window.InputHitTest(new Point(390, 390)) is Visual inside && (inside == popup || popup.IsVisualAncestorOf(inside)), "The popup covers the surface's row.");
            Require(window.InputHitTest(new Point(20, 20)) is not Visual over || over != popup && !popup.IsVisualAncestorOf(over), "The popup does not reach the title row.");

            window.MouseDown(new Point(20, 20), MouseButton.Left);
            window.MouseUp(new Point(20, 20), MouseButton.Left);
            Require(surface.Popups.Count == 0 && hostile.Dismissed == 1, "A press elsewhere hides the popup the plugin keeps open, and the plugin hears it.");
            hostile.Open(2);
            Sync();
            Require(surface.Popups.Count == 0, "A hidden popup's place sent again does not show it.");

            window.MouseMove(new Point(100, 140));
            hostile.Open(3, input: false);
            Sync();
            Require(surface.Popups.Count == 1, "A tooltip shows after input.");
            surface.Focus(NavigationMethod.Pointer);
            E2eWorkspace.Until(() => surface.IsFocused);
            Press(window, Key.Escape, PhysicalKey.Escape);
            Require(heard && surface.Popups.Count == 1, "With only a tooltip shown, the app hears Escape.");
            window.MouseMove(new Point(100, 150));
            hostile.Open(4);
            Sync();
            Require(surface.Popups.Count == 2, "A menu shows after input.");
            Press(window, Key.Escape, PhysicalKey.Escape);
            Require(!heard && surface.Popups.Single().Number == 3, "Escape stays with the plugin and hides its newest popup that takes input, whether or not the plugin closes it.");
            Press(window, Key.Escape, PhysicalKey.Escape);
            Require(heard, "With the menu hidden, the app hears Escape again.");

            title.Focus();
            E2eWorkspace.Until(() => !surface.IsFocused);
            Require(surface.Popups.Count == 0 && hostile.Dismissed == 2, "Focus leaving the surface hides the rest.");

            window.MouseMove(new Point(100, 140));
            hostile.Open(5, dialog: true);
            Sync();
            var dialog = surface.Popups.Single(popup => popup.Dialog);
            // The dialog's frame takes the layer's size on the next layout, and the pointer finds what was last rendered.
            E2eWorkspace.Settle(50);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var close = dialog.Shown.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PluginDialogClose");
            var closeAt = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            Require(closeAt.X is > 0 and < 400 && closeAt.Y is > 40 and < 400, $"A dialog larger than the window keeps its close button in view, not at {closeAt}.");
            window.MouseMove(closeAt);
            window.MouseDown(closeAt, MouseButton.Left);
            window.MouseUp(closeAt, MouseButton.Left);
            Require(surface.Popups.Count == 0, "The frame's close button removes a dialog the plugin keeps open.");
            hostile.Open(5, dialog: true);
            Sync();
            Require(surface.Popups.Count == 0, "A closed dialog's place sent again does not show it.");

            // A dialog the plugin opens long after any input, as after a slow call, still shows: its frame names the plugin.
            E2eWorkspace.Settle(2100);
            hostile.Open(6, dialog: true);
            Sync();
            Require(surface.Popups.Count == 1 && surface.Popups.Single().Dialog, "A dialog that opens more than 2 seconds after input shows.");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A plugin surface whose events the check writes, and which ignores every request.</summary>
    private sealed class HostileSurface : IPluginSurface
    {
        private readonly Channel<SurfaceEvent> events = Channel.CreateUnbounded<SurfaceEvent>();

        public ChannelReader<SurfaceEvent> Events => events.Reader;
        public int Number => 1;
        public int Dismissed { get; private set; }

        /// <summary>Opens or moves a popup as large as the relay allows, from far above and left of the surface.</summary>
        public void Open(int popup, bool input = true, bool dialog = false) =>
            events.Writer.TryWrite(new SurfacePopupChanged(new(1, popup, -100, -100, 5000, 5000, true, input, dialog, dialog ? "Stays open" : "")));

        /// <summary>A frame of the surface with no tiles, which the check waits for to know what came before it applied.</summary>
        public void Frame(long sequence) => events.Writer.TryWrite(new SurfaceFrame(new(1, sequence, 10, 10, 1, 200, 200, true, [], [], sequence == 1), []));

        public void Resize(double width, double height, double scale) { }
        public void Input(SurfaceInput input) { }
        public void Acknowledge(long sequence, int popup) { }
        public void Focus(bool enter, bool backward, bool pointer) { }
        public void Dismiss() => Dismissed++;
        public void Theme(string json) { }
        public void Keyframe() { }

        public ValueTask DisposeAsync()
        {
            events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Waits for the surface's one popup to show in the app with its pixels, and answers it.</summary>
    private static RemoteSurface.RemotePopup Popup(RemoteSurface board)
    {
        RemoteSurface.RemotePopup? found = null;
        E2eWorkspace.Until(() => board.Popups.Count == 1 && (found = board.Popups.Single()) is { Parent: not null, Bounds.Width: > 0 } && board.Snapshot(found.Number).Width > 0);
        return found!;
    }

    internal static int? Clicks(E2eWorkspace app) =>
        app.Workbench.State.Current.PluginSettings.TryGetValue(Id, out var space) && space.ValueKind == JsonValueKind.Object && space.TryGetProperty("clicks", out var clicks) ? clicks.GetInt32() : space.ValueKind == JsonValueKind.Object ? 0 : null;

    internal static RemoteSurface Surface(Visual within, Func<RemoteSurface, bool> fits)
    {
        RemoteSurface? found = null;
        E2eWorkspace.Until(() => (found = within.GetVisualDescendants().OfType<RemoteSurface>().FirstOrDefault(fits)) is { Frames: > 0 });
        return found!;
    }

    internal sealed record Shown(int Width, int Height, byte[] Pixels, Dictionary<string, double[]> Bounds, Dictionary<string, string> Texts);

    /// <summary>
    /// Waits until the pixels the app decoded equal a fresh render in the plugin's process (and differ from
    /// <paramref name="changedFrom"/> when given): lossless tiles leave no difference.
    /// </summary>
    internal static Shown Settle(RemoteSurface surface, string what, byte[]? changedFrom = null, Func<RemoteSurface, UiSessionRelay.RelaySurface?>? relay = null, int popup = 0)
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
            var capture = held.CaptureAsync(Names, popup);
            E2eWorkspace.Until(() => capture.IsCompleted);
            var reference = capture.GetAwaiter().GetResult();
            var (width, height, pixels) = popup == 0 ? surface.Snapshot() : surface.Snapshot(popup);
            var expected = Convert.FromBase64String(reference.Pixels);
            if (width == reference.Width && height == reference.Height && pixels.AsSpan().SequenceEqual(expected) && (changedFrom is null || !pixels.AsSpan().SequenceEqual(changedFrom)))
                return new(width, height, pixels, reference.Bounds, reference.Texts ?? []);
            last = $"app {width}x{height}, plugin {reference.Width}x{reference.Height}, differing bytes {(pixels.Length == expected.Length ? pixels.Where((value, index) => value != expected[index]).Count() : -1)}";
            var wait = Stopwatch.StartNew();
            E2eWorkspace.Until(() => wait.ElapsedMilliseconds > 50);
        }
        throw new InvalidOperationException($"The app's pixels for {what} never matched the plugin's render: {last}.");
    }

    /// <summary>The app's own rendered frame shows the surface's (or a popup's) opaque pixels where it sits.</summary>
    private static void Drawn(TopLevel window, Visual surface, Shown shown)
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

    internal static Point Point(TopLevel window, Visual surface, double[] bounds) =>
        surface.TranslatePoint(new Point(bounds[0] + bounds[2] / 2, bounds[1] + bounds[3] / 2), window) ?? throw new InvalidOperationException("The surface is not in the window.");

    internal static void Click(TopLevel window, Visual surface, double[] bounds)
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