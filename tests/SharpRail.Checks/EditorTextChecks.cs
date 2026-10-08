using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Scintilla;

using SkiaSharp;

namespace SharpRail.Checks;

// Shaping, grapheme clusters and bidirectional layout in the Scintilla editor.
internal static class EditorTextChecks
{
    private const string Family = "👩‍👩‍👧";

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPress(key, modifiers, PhysicalKey.None, null); window.KeyRelease(key, modifiers, PhysicalKey.None, null); Pump(); }
    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private static nint Send(ScintillaEditor editor, ScintillaMessage message, nint w = 0, nint l = 0) => editor.Document.Send(message, w, l);
    private static double X(ScintillaEditor editor, int position) => Send(editor, ScintillaMessage.PointXFromPosition, 0, position);
    private static int Caret(ScintillaEditor editor) => (int)Send(editor, ScintillaMessage.GetCurrentPos);
    private static int PositionAt(ScintillaEditor editor, double x, int line = 0) =>
        (int)Send(editor, ScintillaMessage.PositionFromPoint, (nint)Math.Round(x), (nint)(Send(editor, ScintillaMessage.TextHeight) * line + 2));

    internal static void Run()
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP macOS Scintilla text layout"); return; }
        using var editor = new ScintillaEditor();
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump(); editor.Focus();
        try
        {
            Clusters(window, editor);
            Bidirectional(window, editor);
            Rendering(window, editor);
            Wrapping(window, editor);
            LineStyles(window, editor);
            RecordingReuse(window, editor);
        }
        finally { window.Close(); }
        Sample();
    }

    // A capture for visual review in the application's font.
    private static void Sample()
    {
        string[] lines =
        [
            "Emoji: family 👩‍👩‍👧 flag 🇺🇦 keycap 1️⃣ heart ❤️ skin 👍🏽",
            "Marks: é ñ Ω̃ — CJK: 漢字かなカナ 한국어",
            "Ligatures: -> => != === <= >= :: </>",
            "Hebrew in Latin: abc שלום עולם 123 def",
            "Arabic: مرحبا بالعالم (2025) hello",
        ];
        using var editor = new ScintillaEditor(string.Join('\n', lines), EditorChecks.Font);
        var window = new Window { Width = 720, Height = 200, Content = editor };
        window.Show(); Pump();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Directory.CreateDirectory(".bench");
            frame!.Save(".bench/scintilla-text.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private static void Clusters(Window window, ScintillaEditor editor)
    {
        editor.Text = $"{Family}\n👩\né\ne";
        Pump();
        var family = X(editor, Bytes(Family)) - X(editor, 0);
        var single = X(editor, Bytes($"{Family}\n👩")) - X(editor, Bytes($"{Family}\n"));
        Require(single > 4 && Math.Abs(family - single) < 1, $"A ZWJ emoji sequence must shape to one glyph ({family} vs {single}).");
        var markStart = Bytes($"{Family}\n👩\n");
        var accented = X(editor, markStart + Bytes("é")) - X(editor, markStart);
        var plain = X(editor, markStart + Bytes("é\ne")) - X(editor, markStart + Bytes("é\n"));
        Require(Math.Abs(accented - plain) < 1, $"A combining mark must not add an advance ({accented} vs {plain}).");

        editor.Text = $"a{Family}b";
        Press(window, Key.End); Press(window, Key.Left);
        Require(Caret(editor) == 1 + Bytes(Family), "Left must stop before the last character.");
        Press(window, Key.Left);
        Require(Caret(editor) == 1, $"Left must step over a whole emoji sequence (caret {Caret(editor)}).");
        Press(window, Key.Right);
        Require(Caret(editor) == 1 + Bytes(Family), "Right must step over a whole emoji sequence.");
        Press(window, Key.Right); Press(window, Key.Left, RawInputModifiers.Shift); Press(window, Key.Left, RawInputModifiers.Shift);
        Require(editor.Document.Text(true) == Family + "b", "Shift+Left must extend the selection by grapheme.");
        Press(window, Key.End); Press(window, Key.Left); Press(window, Key.Back);
        Require(editor.Text == "ab", $"Backspace must delete a whole emoji sequence ({editor.Text}).");
        Press(window, Key.Z, RawInputModifiers.Meta);
        Require(editor.Text == $"a{Family}b", "Undoing a grapheme deletion must restore it in one step.");

        editor.Text = "a🇺🇦b";
        Press(window, Key.Home); Press(window, Key.Right); Press(window, Key.Delete);
        Require(editor.Text == "ab", "Delete must remove a whole flag.");
        editor.Text = "xéy";
        Press(window, Key.End); Press(window, Key.Left); Press(window, Key.Left);
        Require(Caret(editor) == 1, "Left must step over a base letter with its combining mark.");

        editor.Text = $"a{Family}b";
        Pump();
        var (left, right) = (X(editor, 1), X(editor, 1 + Bytes(Family)));
        for (var x = left + 1; x < right; x += 2)
        {
            var hit = PositionAt(editor, x);
            Require(hit == 1 || hit == 1 + Bytes(Family), $"Clicking inside an emoji sequence must not land inside it (x {x}: {hit}).");
        }
        Console.WriteLine("PASS Scintilla HarfBuzz clusters, grapheme caret movement, deletion and hit testing");
    }

    private static void Bidirectional(Window window, ScintillaEditor editor)
    {
        // Logical "abc " then Hebrew shin, lamed, vav, final mem: the Hebrew run reads right to left.
        editor.Text = "abc שלום";
        Pump();
        int[] hebrew = [4, 6, 8, 10];
        for (var i = 1; i < hebrew.Length; i++)
            Require(X(editor, hebrew[i]) < X(editor, hebrew[i - 1]) - 2, $"Hebrew letters must advance leftwards ({X(editor, hebrew[i - 1])} → {X(editor, hebrew[i])}).");
        Require(X(editor, 10) > X(editor, 3) + 2, "The Hebrew run must follow the Latin text in a left-to-right line.");
        var shin = (Left: X(editor, 6), Right: X(editor, 4));
        var hit = PositionAt(editor, shin.Right - 2);
        Require(hit == 4, $"The right half of a right-to-left letter must place the caret before it ({hit}).");
        hit = PositionAt(editor, shin.Left + 2);
        Require(hit == 6, $"The left half of a right-to-left letter must place the caret after it ({hit}).");

        editor.Text = "שלום!";
        Pump();
        Require(X(editor, 8) >= X(editor, 0) - 1, "In a left-to-right line, trailing punctuation follows the Hebrew run.");
        editor.Direction = ScintillaTextDirection.Auto;
        Pump();
        Require(X(editor, 8) < X(editor, 0) - 20, "An automatic direction must order a Hebrew line right to left.");
        editor.Direction = ScintillaTextDirection.RightToLeft;
        editor.Text = "abc!";
        Pump();
        // "!" now sits left of "abc", so the end of the line is its left edge.
        Require(X(editor, 4) < X(editor, 0) - 2 && X(editor, 1) > X(editor, 0), "A right-to-left base must move trailing punctuation to the left of Latin text.");
        editor.Direction = ScintillaTextDirection.LeftToRight;

        // Committed input, as from an IME, can be several non-ASCII characters at once.
        editor.Text = "";
        window.KeyTextInput("שלום"); Pump();
        window.KeyTextInput(" world"); Pump();
        Require(editor.Text == "שלום world", "Typing mixed-direction text must keep logical order.");
        var copy = window.Clipboard!.SetTextAsync("émigré שלום");
        while (!copy.IsCompleted) Pump();
        editor.SelectAll(); Press(window, Key.V, RawInputModifiers.Meta);
        for (var i = 0; i < 20 && editor.Text != "émigré שלום"; i++) Pump();
        Require(editor.Text == "émigré שלום", $"Pasting text that starts with a non-ASCII character failed ({editor.Text}).");
        Console.WriteLine("PASS Scintilla bidirectional caret positions, hit testing, base directions and editing");
    }

    // Styled letters must be painted in visual order, and the selection behind its visual interval.
    private static void Rendering(Window window, ScintillaEditor editor)
    {
        editor.Colors = new ScintillaColors(Colors.Black, Colors.White, Colors.Gray, Color.FromRgb(0, 255, 0));
        editor.Text = "abc שלום";
        Pump();
        const int red = 1, blue = 2;
        Send(editor, ScintillaMessage.StyleSetFore, red, 0x0000ff);
        Send(editor, ScintillaMessage.StyleSetFore, blue, 0xff0000);
        Send(editor, ScintillaMessage.StartStyling, 4);
        Send(editor, ScintillaMessage.SetStyling, 2, red);
        Send(editor, ScintillaMessage.SetStyling, 2, blue);
        Send(editor, ScintillaMessage.SetSel, 12, 12);
        editor.InvalidateVisual(); Pump();
        var pixels = Capture(window);
        var row = Row(editor, window);
        var redX = Columns(pixels, row, c => c.Red > 170 && c.Green < 100 && c.Blue < 100);
        var blueX = Columns(pixels, row, c => c.Blue > 170 && c.Red < 100 && c.Green < 100);
        Require(redX.Count > 0 && blueX.Count > 0, "Styled Hebrew letters were not painted.");
        Require(redX.Average() > blueX.Average() + 3, $"The first logical Hebrew letter must be painted right of the second ({redX.Average()} vs {blueX.Average()}).");

        // Selecting only shin must highlight its visual interval at the right end of the line.
        Send(editor, ScintillaMessage.SetSel, 4, 6);
        editor.InvalidateVisual(); Pump();
        pixels = Capture(window);
        var greenX = Columns(pixels, row, c => c.Green > 200 && c.Red < 120 && c.Blue < 120);
        Require(greenX.Count > 0, "The selection background was not painted.");
        var offset = SelectionOrigin(editor, window);
        Require(greenX.Min() >= offset + X(editor, 6) - 2 && greenX.Max() <= offset + X(editor, 4) + 2,
            $"The selection must cover shin's visual interval ({greenX.Min()}–{greenX.Max()} vs {offset + X(editor, 6)}–{offset + X(editor, 4)}).");

        // The caret's line is highlighted only when the owner names a colour for it, and the caret takes its own.
        Send(editor, ScintillaMessage.SetSel, 0, 0);
        editor.Colors = editor.Colors with { CurrentLine = Color.FromRgb(0, 0, 255), Caret = Color.FromRgb(255, 0, 0) };
        editor.Focus(); Pump(); Pump();
        using (var lined = Capture(window))
        {
            var band = lined.GetPixel(lined.Width - 40, row);
            Require(band.Blue > 200 && band.Red < 60 && band.Green < 60, $"The current line must be painted in its colour to the right edge (saw {band}).");
        }
        editor.Colors = editor.Colors with { CurrentLine = null };
        Pump(); Pump();
        using (var plain = Capture(window))
        {
            var band = plain.GetPixel(plain.Width - 40, row);
            Require(band is { Red: > 240, Green: > 240, Blue: > 240 }, $"Without a current-line colour the caret's line keeps the background (saw {band}).");
        }
        Console.WriteLine("PASS Scintilla paints reordered text and selection at their visual positions");
    }

    private static void Wrapping(Window window, ScintillaEditor editor)
    {
        editor.WrapWidth = 160;
        editor.Text = string.Join(' ', Enumerable.Repeat("שלום world مرحبا", 6));
        Pump();
        Require(editor.WrapCount(0) > 1, "A long bidirectional line must wrap.");
        Press(window, Key.End); window.KeyTextInput("!"); Pump();
        Require(editor.Text.EndsWith("!", StringComparison.Ordinal), "Typing at the end of a wrapped bidirectional line failed.");
        editor.WrapWidth = double.PositiveInfinity;
        Console.WriteLine("PASS Scintilla wraps and edits bidirectional lines");
    }

    private static void LineStyles(Window window, ScintillaEditor editor)
    {
        editor.Text = "plain\nMMMMMMMM\nMMMMMMMM";
        editor.LineStyles = [new(Colors.Red), new(Colors.Black, Color.FromRgb(0, 0, 255))];
        editor.StyleLines([-1, 0, 1]);
        Pump(); Pump();
        using var pixels = Capture(window);
        var row = Row(editor, window);
        var height = (int)Send(editor, ScintillaMessage.TextHeight);
        var red = Columns(pixels, row + height, color => color.Red > 200 && color.Green < 80 && color.Blue < 80).Count;
        var band = pixels.GetPixel(pixels.Width - 40, row + 2 * height);
        Require(red > 0, "A line style must colour its line's text.");
        Require(band.Blue > 200 && band.Red < 60, $"A line style's background must fill its line to the right edge (saw {band}).");
        editor.Text = string.Join(' ', Enumerable.Range(0, 40).Select(i => i % 3 == 0 ? "tab\tword" : "lorem")) + "\nnext";
        editor.WrapWidth = 300;
        editor.StyleLines([1, 1]);
        Pump(); Pump();
        editor.LabelLines(["12", null]); Pump(); Pump();
        using (var wrapped = Capture(window))
        {
            var top = Row(editor, window) - height / 2;
            var left = (int)(editor.TranslatePoint(new Point(0, 0), window)!.Value.X + Send(editor, ScintillaMessage.PointXFromPosition, 0, 0));
            var gaps = 0;
            for (var y = top + 1; y < top + editor.WrapCount(0) * height - 1; y++)
                for (var x = left; x < left + 280; x++)
                    if (wrapped.GetPixel(x, y) is { Red: > 200, Green: > 200 }) gaps++;
            Require(editor.WrapCount(0) > 3 && gaps == 0, $"A wrapped line's background must cover wrap breaks and tab stops ({gaps} gap pixels).");
        }
        editor.ShowLineNumbers = true;
        editor.WrapWidth = double.PositiveInfinity;
        editor.LineStyles = []; editor.StyleLines([]);
        Console.WriteLine("PASS Scintilla line styles colour text and fill line backgrounds");
    }

    // A wheel step inside the first visible line only shifts the last recording; an edit records again.
    private static void RecordingReuse(Window window, ScintillaEditor editor)
    {
        editor.Text = string.Join('\n', Enumerable.Range(0, 200).Select(index => $"line {index}"));
        Pump(); Pump();
        var height = Send(editor, ScintillaMessage.TextHeight);
        // The first partial line adds a row below the viewport, which is recorded once.
        window.MouseWheel(new Point(200, 100), new Vector(0, -height / 4 / 50.0)); Pump();
        var recordings = editor.Recordings;
        window.MouseWheel(new Point(200, 100), new Vector(0, -height / 4 / 50.0)); Pump();
        Require(editor.VerticalPixelOffset > 0 && editor.VerticalPixelOffset < height && editor.Recordings == recordings,
            $"Scrolling within a line must reuse the recording ({editor.Recordings - recordings} new, offset {editor.VerticalPixelOffset}).");
        // Leaving the line must record again, although the partial-row resize already asked Scintilla to redraw.
        window.MouseWheel(new Point(200, 100), new Vector(0, -3 * height / 50.0)); Pump();
        Require(editor.Recordings > recordings, $"Scrolling to another line must record again (top {editor.FirstVisibleLine}).");
        recordings = editor.Recordings;
        editor.Text = "changed"; Pump();
        Require(editor.Recordings > recordings, "An edit must record the editor again.");
        Console.WriteLine("PASS Scintilla reuses its recording while scrolling within a line");
    }

    private static SKBitmap Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The editor did not render.");
        using var stream = new MemoryStream();
        frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }

    // The frame's y for the middle of the first text line, and its x for the editor's text origin.
    private static int Row(ScintillaEditor editor, Window window)
    {
        var origin = editor.TranslatePoint(new Point(0, 0), window)!.Value;
        return (int)(origin.Y + Send(editor, ScintillaMessage.TextHeight) / 2);
    }
    private static double SelectionOrigin(ScintillaEditor editor, Window window) => editor.TranslatePoint(new Point(0, 0), window)!.Value.X;

    private static List<int> Columns(SKBitmap pixels, int row, Func<SKColor, bool> match)
    {
        var columns = new List<int>();
        for (var y = row - 4; y <= row + 4; y++)
            for (var x = 0; x < pixels.Width; x++)
                if (match(pixels.GetPixel(x, y))) columns.Add(x);
        return columns;
    }
}