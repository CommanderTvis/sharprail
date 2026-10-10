using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Platform;
using Avalonia.Threading;

using SharpRail.Scintilla;

using SkiaSharp;

namespace SharpRail.Checks;

internal static class EditorChecks
{
    // The application's editor font; these checks' pixel scrolling assumes its line height.
    internal static SKTypeface Font => font.Value;
    private static readonly Lazy<SKTypeface> font = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-Regular.ttf"));
        return SKTypeface.FromStream(stream);
    });

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Key(Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPress(key, modifiers, PhysicalKey.None, null); window.KeyRelease(key, modifiers, PhysicalKey.None, null); Pump(); }

    internal static void Run()
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP macOS Scintilla editor"); return; }
        using var editor = new ScintillaEditor("alpha\nβeta\n😀 end", Font);
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump(); editor.Focus();
        try
        {
            Key(window, Avalonia.Input.Key.End);
            window.KeyTextInput("!"); Pump();
            Require(editor.Text == "alpha!\nβeta\n😀 end", "Scintilla text input did not edit at the caret.");
            Require(editor.IsModified, "Scintilla failed to track modifications.");
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Meta);
            Require(editor.Text == "alpha\nβeta\n😀 end" && !editor.IsModified, "Undo did not return to the save point.");
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Meta | RawInputModifiers.Shift);
            Require(editor.Text.StartsWith("alpha!", StringComparison.Ordinal), "macOS redo failed.");
            editor.Text = "a😀éz";
            Key(window, Avalonia.Input.Key.End);
            Key(window, Avalonia.Input.Key.Left);
            Key(window, Avalonia.Input.Key.Back);
            Require(editor.Text == "a😀z", "UTF-8 backspace split a multibyte scalar.");
            Key(window, Avalonia.Input.Key.Back);
            Require(editor.Text == "az", "UTF-8 backspace split an emoji.");
            editor.Text = "first\nβ😀 caret line\nlast";
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.RaiseEvent(request);
            var ime = request.Client!;
            Key(window, Avalonia.Input.Key.Down);
            Require(ime.SurroundingText == "β😀 caret line", "IME surrounding text is not scoped to the caret line.");
            ime.Selection = new(1, 3);
            Require(ime.Selection == new TextSelection(1, 3), "IME selection did not round-trip through UTF-8 positions.");
            window.KeyTextInput("!"); Pump();
            Require(editor.Text == "first\nβ! caret line\nlast", "IME selection addressed the wrong text.");
            editor.SelectAll(); window.KeyTextInput("replacement\nsecond line"); Pump();
            Require(editor.Text == "replacement\nsecond line", "Typing did not replace the selection.");
            Key(window, Avalonia.Input.Key.A, RawInputModifiers.Meta);
            Key(window, Avalonia.Input.Key.C, RawInputModifiers.Meta);
            var copy = window.Clipboard!.TryGetTextAsync();
            while (!copy.IsCompleted) Pump();
            Require(copy.GetAwaiter().GetResult() == editor.Text, "Clipboard copy lost text.");
            Key(window, Avalonia.Input.Key.X, RawInputModifiers.Meta);
            Require(editor.Text == "", "Clipboard cut failed.");
            Key(window, Avalonia.Input.Key.V, RawInputModifiers.Meta);
            Require(editor.Text == "replacement\nsecond line", "Clipboard paste failed.");
            editor.IsReadOnly = true;
            window.KeyTextInput("no"); Key(window, Avalonia.Input.Key.Back);
            Require(editor.Text == "replacement\nsecond line", "Read-only editor accepted input.");
            editor.IsReadOnly = false;
            editor.Text = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"line {i}: public class Sample {{ }}"));
            Pump();
            using var before = window.CaptureRenderedFrame();
            Require(before is not null, "Skia editor did not render.");
            Directory.CreateDirectory(".bench");
            before!.Save(".bench/scintilla-before.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.MouseWheel(new Point(250, 150), new Vector(0, -8)); Pump();
            Require(editor.FirstVisibleLine > 0, "Wheel input did not scroll Scintilla's viewport.");
            Require(editor.VerticalScroll.Maximum > 0 && editor.VerticalScroll.Value == editor.FirstVisibleLine, "Scroll extents did not track the viewport.");
            // Wheel deltas scroll by pixels (50 per unit, like the app's other scroll views), not whole lines,
            // so trackpad and momentum scrolling stay smooth instead of jumping several lines per event.
            var top = editor.FirstVisibleLine;
            var offset = editor.VerticalPixelOffset;
            window.MouseWheel(new Point(250, 150), new Vector(0, -0.1)); Pump();
            Require(editor.FirstVisibleLine == top && Math.Abs(editor.VerticalPixelOffset - offset - 5) < 0.5,
                $"A small wheel delta must move the view by pixels within a line ({offset} -> {editor.VerticalPixelOffset}).");
            for (var i = 0; i < 9; i++) window.MouseWheel(new Point(250, 150), new Vector(0.02, -0.1));
            Pump();
            Require(Math.Abs(editor.VerticalPixelOffset - offset - 50) < 0.5 && editor.FirstVisibleLine > top,
                "Fractional trackpad deltas did not accumulate into vertical scrolling.");
            editor.ScrollToLine(10); Pump();
            Require(editor.FirstVisibleLine == 10 && editor.VerticalScroll.Value == 10, "Scrollbar position did not scroll the editor.");
            using var after = window.CaptureRenderedFrame();
            after!.Save(".bench/scintilla-after.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Require(!File.ReadAllBytes(".bench/scintilla-before.png").SequenceEqual(File.ReadAllBytes(".bench/scintilla-after.png")), "Scrolling did not change rendered editor content.");
            window.MouseDown(new Point(110, 50), MouseButton.Left);
            window.MouseMove(new Point(250, 50));
            window.MouseUp(new Point(250, 50), MouseButton.Left); Pump();
            var prior = editor.Text;
            window.KeyTextInput("selected"); Pump();
            Require(editor.Text != prior && editor.Text.Contains("selected", StringComparison.Ordinal), "Pointer selection did not accept typing.");
            using var second = new ScintillaEditor("independent", Font);
            Require(second.Text == "independent" && editor.Text != second.Text, "Editor documents share mutable state.");
            editor.MarkSaved(); Require(!editor.IsModified, "Explicit save point did not clear modified state.");
            Console.WriteLine("PASS macOS Scintilla input, UTF-8, undo/redo, clipboard, scrolling, Skia drawing and independent documents");
        }
        finally { window.Close(); }
        WrappedScrolling();
        TextSizeAndWrapping();
        TouchAndSoftKeyboard();
        HorizontalScrolling();
    }

    private static void HorizontalScrolling()
    {
        using var editor = new ScintillaEditor(new string('W', 200), Font);
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump();
        try
        {
            Require(editor.HorizontalScroll.Maximum > 0, "Long unwrapped lines need a horizontal range.");
            editor.ScrollToX(double.MaxValue); Pump();
            Require(editor.HorizontalScroll.Value == editor.HorizontalScroll.Maximum, "Horizontal API overscrolled past the text.");
            window.MouseWheel(new Point(250, 150), new Vector(-1000, 0)); Pump();
            Require(editor.HorizontalScroll.Value == editor.HorizontalScroll.Maximum, "Horizontal wheel overscrolled past the text.");
            editor.WrapWidth = 400;
            Require(editor.Document.Send(ScintillaMessage.GetXOffset) == 0, "Enabling soft wrap retained the horizontal offset.");
            Pump();
            editor.ScrollToX(1000);
            window.MouseWheel(new Point(250, 150), new Vector(-1000, -0.1)); Pump();
            Require(editor.HorizontalScroll is { Maximum: 0, Value: 0 } && editor.Document.Send(ScintillaMessage.GetXOffset) == 0,
                "Soft-wrapped text must reject horizontal wheel and API scrolling.");
            editor.WrapWidth = double.PositiveInfinity; Pump();
            editor.ScrollToX(1000); Pump();
            window.Width = 2400; Pump();
            Require(editor.HorizontalScroll is { Maximum: 0, Value: 0 }, "Widening the viewport left unnecessary horizontal scrolling.");
            window.Width = 640; Pump();
            editor.ScrollToX(1000); Pump();
            editor.Text = "short"; Pump();
            Require(editor.HorizontalScroll is { Maximum: 0, Value: 0 }, "Shortening the document retained the old horizontal extent.");
            Console.WriteLine("PASS macOS Scintilla horizontal scrolling is bounded and disabled by soft wrap");
        }
        finally { window.Close(); }
    }

    // Wrapped documents scroll in display lines; the range must cover every wrapped line,
    // including lines Scintilla wraps during idle rather than while painting.
    // What a pinch drives: one text size for every editor, a wrap column that keeps its count of characters,
    // and, where sideways scrolling is a chore, wrapping at the pane's edge without any wrap width.
    private static void TextSizeAndWrapping()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var line = new string('x', 600);
        using var bounded = new ScintillaEditor(line, Font) { WrapWidth = 200 };
        using var free = new ScintillaEditor(line, Font);
        var window = new Window { Width = 2000, Height = 320, Content = new Grid { ColumnDefinitions = new("*,*"), Children = { bounded, free } } };
        Grid.SetColumn(free, 1);
        window.Show(); Pump();
        try
        {
            static void Settle() { for (var pass = 0; pass < 20; pass++) { using var slice = new CancellationTokenSource(10); Dispatcher.UIThread.MainLoop(slice.Token); Pump(); } }
            Settle();
            var rows = bounded.WrapCount(0);
            Require(rows > 1 && free.WrapCount(0) == 1, $"A wrap width must wrap and its absence must not ({rows}, {free.WrapCount(0)}).");
            ScintillaEditor.TextSize = ScintillaEditor.DefaultTextSize * 2;
            Settle();
            Require(bounded.WrapCount(0) == rows, $"A wrap column must keep its characters at another text size ({rows} then {bounded.WrapCount(0)}).");
            ScintillaEditor.TextSize = 1000;
            Require(ScintillaEditor.TextSize == ScintillaEditor.MaximumTextSize, "The text size must stay within its range.");
            ScintillaEditor.TextSize = ScintillaEditor.DefaultTextSize;
            ScintillaEditor.WrapAlways = true;
            free.WrapWidth = double.PositiveInfinity;
            Settle();
            var edge = free.WrapCount(0);
            Require(edge > 1, "Always-wrap must wrap an unbounded editor at its pane's edge.");
            ScintillaEditor.TextSize = ScintillaEditor.DefaultTextSize * 2;
            Settle();
            Require(free.WrapCount(0) > edge, $"Larger text must take more rows of the same pane ({edge} then {free.WrapCount(0)}).");
            Console.WriteLine("PASS Scintilla text size: one size for every editor, wrap columns keep their characters, always-wrap follows the pane");
        }
        finally { ScintillaEditor.WrapAlways = false; ScintillaEditor.TextSize = ScintillaEditor.DefaultTextSize; window.Close(); }
    }

    private static void WrappedScrolling()
    {
        if (!OperatingSystem.IsMacOS()) return;
        const int lines = 400;
        var line = string.Join(' ', Enumerable.Range(1, 60).Select(index => $"segment-{index:00}"));
        using var editor = new ScintillaEditor(string.Join('\n', Enumerable.Repeat(line, lines)), Font) { WrapWidth = 400 };
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump();
        try
        {
            // Idle wrapping grows the range in slices; wait until it settles.
            var deadline = Awake.Now.AddSeconds(10);
            for (var stable = 0; stable < 10 && Awake.Now < deadline;)
            {
                var before = editor.VerticalScroll;
                using var slice = new CancellationTokenSource(20);
                Dispatcher.UIThread.MainLoop(slice.Token);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                stable = editor.VerticalScroll == before ? stable + 1 : 0;
            }
            Require(editor.VerticalScroll.Maximum + editor.VerticalScroll.Viewport >= lines * 2,
                $"The wrapped scroll range must cover every display line ({editor.VerticalScroll}).");
            editor.ScrollToLine(editor.VerticalScroll.Maximum); Pump();
            Require(editor.FirstVisibleLine == (int)editor.VerticalScroll.Maximum, $"Scrolling to the end of the range must reach it (first {editor.FirstVisibleLine}, {editor.VerticalScroll}).");
            Console.WriteLine("PASS macOS Scintilla wrapped documents scroll over every display line");
        }
        finally { window.Close(); }
    }

    // What an Android client drives: a finger taps, drags and holds, and Avalonia's input connection edits through
    // offsets into the surrounding text, deleting with the Delete key and committing Enter as text.
    private static void TouchAndSoftKeyboard()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var editor = new ScintillaEditor("one\r\ntwo\r\nthree", Font) { InputSpansNeighbours = true };
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump();
        try
        {
            nint Send(ScintillaMessage message) => editor.Document.Send(message);
            (nint, nint) Selected() => (Send(ScintillaMessage.GetSelectionStart), Send(ScintillaMessage.GetSelectionEnd));
            void Touch(Point from, Point? to = null, TimeSpan? held = null)
            {
                var touch = window.TouchBegin(from, RawInputModifiers.None);
                if (held is { } duration)
                {
                    using var wait = new CancellationTokenSource(duration);
                    Dispatcher.UIThread.MainLoop(wait.Token);
                }
                for (var step = 1; to is { } end && step <= 5; step++) window.TouchMove(touch, from + (end - from) * step / 5, RawInputModifiers.None);
                window.TouchEnd(touch, to ?? from, RawInputModifiers.None); Pump();
            }
            var row = (double)Send(ScintillaMessage.TextHeight);

            Touch(new Point(600, row * 1.5));
            Require(editor.IsFocused && Selected() == (8, 8), $"A tap must focus the editor and place the caret ({Selected()}).");
            var ime = editor.InputMethodClient;
            Require(ime.SurroundingText == "one\ntwo\nthree" && ime.Selection == new TextSelection(7, 7),
                $"The soft keyboard must see the neighbouring lines with single-character breaks ({ime.Selection}).");

            Key(window, Avalonia.Input.Key.Home);
            ime.Selection = new(3, 4);
            Key(window, Avalonia.Input.Key.Delete);
            ime.Selection = new(3, 3);
            Require(editor.Text == "onetwo\r\nthree" && Selected() == (3, 3), $"Backspace at a line start must join the lines ({Selected()}).");

            window.KeyTextInput("\n");
            ime.Selection = new(4, 4);
            Pump();
            Require(editor.Text == "one\r\ntwo\r\nthree" && Selected() == (5, 5), $"Enter must insert the document's line ending and keep the caret after it ({Selected()}).");

            Key(window, Avalonia.Input.Key.Down);
            // The platform's read after the caret moved still addresses the old text; the new one arrives afterwards.
            Require(ime.Selection == new TextSelection(8, 8), $"An edit in progress must keep its offsets ({ime.Selection}).");
            Pump();
            Require(ime.SurroundingText == "two\nthree" && ime.Selection == new TextSelection(4, 4), "The surrounding text must follow the caret once an edit has finished.");

            Touch(new Point(80, row * 1.5), held: Application.Current!.PlatformSettings!.HoldWaitDuration + TimeSpan.FromMilliseconds(250));
            Require(Selected() == (5, 8), $"A long press must select the word under the finger and lifting must keep it ({Selected()}).");

            editor.Text = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"line {i}"));
            Pump();
            Touch(new Point(250, 250), new Point(250, 60));
            var (start, end) = Selected();
            Require(editor.FirstVisibleLine > 0 && start == end, $"A finger drag must scroll instead of selecting (first {editor.FirstVisibleLine}, {start}..{end}).");

            editor.IsReadOnly = true;
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.RaiseEvent(request);
            Require(request.Client is not null, "A read-only editor keeps its input client on desktop.");
            Console.WriteLine("PASS Scintilla touch tap, drag scrolling, long-press selection and soft-keyboard edits");
        }
        finally { window.Close(); }
    }
}