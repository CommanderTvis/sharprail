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
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Fonts/JetBrainsMono-Regular.ttf"));
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
    }

    // Wrapped documents scroll in display lines; the range must cover every wrapped line,
    // including lines Scintilla wraps during idle rather than while painting.
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
            var deadline = DateTime.UtcNow.AddSeconds(10);
            for (var stable = 0; stable < 10 && DateTime.UtcNow < deadline;)
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
}
