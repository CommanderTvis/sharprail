using System.Text;

using SharpRail.Scintilla.Core;

using SkiaSharp;

namespace SharpRail.Scintilla.View;

// Painting straight onto the canvas (upstream EditView::PaintText, DrawLine and MarginView::PaintMargin, with
// Native/EditView.patch: text and backgrounds positioned through the shaped layout). No buffered pixmaps.
internal sealed partial class EditView
{
    // Upstream defaults when no selection element colour is set.
    private static readonly SKColor DefaultSelectionBack = new(0xc0, 0xc0, 0xc0);
    private static readonly SKColor DefaultInactiveSelectionBack = new(0xd8, 0xd8, 0xd8);
    // Upstream marginNumberPadding: gap right of line numbers and margin text.
    private const float MarginPadding = 3;

    private readonly SKPaint paint = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

    /// <summary>Paints margins, backgrounds, selection, text, indicators and caret for the viewport.</summary>
    public void Paint(SKCanvas canvas, PaintState state)
    {
        var style = Metrics(state.Style);
        var client = state.Client;
        var lineHeight = style.LineHeight;
        var textAreaLeft = client.Left + style.FixedColumnWidth;
        var textAreaRight = Math.Max(textAreaLeft, client.Right - style.RightMarginWidth);
        var textLeft = textAreaLeft - state.XOffset;
        var defaultBack = style.Styles[ViewStyle.Default].Back;

        canvas.Save();
        canvas.ClipRect(client);
        Fill(canvas, client, defaultBack);

        var document = state.Document;
        var caret = state.Selection.MainCaret.Position;
        var caretLine = document.LineFromPosition(caret);
        int selStart = state.Selection.Start.Position, selEnd = state.Selection.End.Position;
        var rows = new List<(int Line, int Sub, float Top)>();

        canvas.Save();
        canvas.ClipRect(new SKRect(textAreaLeft, client.Top, textAreaRight, client.Bottom));
        var displayLine = Math.Max(0, state.TopDisplayLine);
        for (var top = client.Top; top < client.Bottom && displayLine < state.Display.LinesDisplayed; top += lineHeight, displayLine++)
        {
            var line = state.Display.DocFromDisplay(displayLine);
            if (line >= document.LinesTotal) break;
            var sub = displayLine - state.Display.DisplayFromDoc(line);
            rows.Add((line, sub, top));
            var layout = Layout(document, style, line, state.WrapWidth);
            if (sub >= layout.Count) continue;
            var row = new Row(layout, sub, document.LineStart(line), top, textLeft, textAreaRight, top + style.Ascent);
            var caretLineBack = line == caretLine && state.Focused && style.Elements.TryGetValue(Element.CaretLineBack, out var c) ? c : (SKColor?)null;
            PaintBackground(canvas, style, row, caretLineBack);
            PaintSelection(canvas, state, style, row, selStart, selEnd, document.LineEnd(line));
            PaintText(canvas, state, style, row, selStart, selEnd);
            PaintIndicators(canvas, document, style, row);
            if (state.Focused && state.CaretVisible && line == caretLine)
            {
                var offset = caret - row.LineStart;
                if (offset >= 0 && offset <= layout.Text.Length && layout.SubLineOf(offset) == sub)
                {
                    var x = textLeft + row.Shape.XFromPosition(offset - row.Start);
                    var colour = style.Elements.TryGetValue(Element.Caret, out var caretColour) ? caretColour : style.CaretFore;
                    Fill(canvas, new SKRect(x, top, x + 1, top + lineHeight), colour);
                }
            }
        }
        canvas.Restore();
        if (rows.Count > 0) Trim(rows[0].Line, rows[^1].Line);

        PaintMargins(canvas, document, style, client, rows, lineHeight);
        if (style.RightMarginWidth > 0) Fill(canvas, new SKRect(textAreaRight, client.Top, client.Right, client.Bottom), defaultBack);
        canvas.Restore();
    }

    private sealed class Row(LineLayout layout, int sub, int lineStart, float top, float textLeft, float right, float baseline)
    {
        public LineLayout Layout => layout;
        public int Sub => sub;
        public LineShape Shape => layout.Shapes[sub];
        public bool Last => sub == layout.Count - 1;
        /// <summary>Sub-line byte range within the line.</summary>
        public int Start => layout.Starts[sub];
        public int End => layout.Starts[sub + 1];
        public int LineStart => lineStart;
        public float Top => top;
        public float TextLeft => textLeft;
        public float Right => right;
        public float Baseline => baseline;
    }

    private void PaintBackground(SKCanvas canvas, ViewStyle style, Row row, SKColor? caretLineBack)
    {
        var bottom = row.Top + style.LineHeight;
        var rowBack = caretLineBack ?? style.Styles[ViewStyle.Default].Back;
        Fill(canvas, new SKRect(row.TextLeft, row.Top, row.Right, bottom), rowBack);
        var layout = row.Layout;
        if (caretLineBack is null)
        {
            foreach (var (start, end, s) in StyleRuns(layout, row.Start, row.End))
            {
                var back = style.Styles[s].Back;
                if (back == rowBack) continue;
                foreach (var interval in row.Shape.RangeIntervals(start - row.Start, end - row.Start))
                    Fill(canvas, new SKRect(row.TextLeft + interval.Left, row.Top, row.TextLeft + interval.Right, bottom), back);
            }
        }
        // EOL-filled: the line's last style fills to the right edge (the line end style when the line is empty).
        if (caretLineBack is null)
        {
            var lastStyle = layout.Styles.Length > 0 ? style.Styles[layout.Styles[^1]] : null;
            if (lastStyle is { EolFilled: true })
                Fill(canvas, new SKRect(row.TextLeft + row.Shape.Width, row.Top, row.Right, bottom), lastStyle.Back);
        }
    }

    private void PaintSelection(SKCanvas canvas, PaintState state, ViewStyle style, Row row, int selStart, int selEnd, int lineEnd)
    {
        if (selStart == selEnd) return;
        var back = state.Focused
            ? style.Elements.GetValueOrDefault(Element.SelectionBack, DefaultSelectionBack)
            : style.Elements.GetValueOrDefault(Element.SelectionInactiveBack, DefaultInactiveSelectionBack);
        var bottom = row.Top + style.LineHeight;
        int start = Math.Max(selStart - row.LineStart, row.Start), end = Math.Min(selEnd - row.LineStart, row.End);
        if (start < end)
            foreach (var interval in row.Shape.RangeIntervals(start - row.Start, end - row.Start))
                Fill(canvas, new SKRect(row.TextLeft + interval.Left, row.Top, row.TextLeft + interval.Right, bottom), back);
        // A selection running past the line end shows the end of line as a space-wide block.
        if (row.Last && selStart <= lineEnd && selEnd > lineEnd)
        {
            var x = row.TextLeft + row.Shape.Width;
            Fill(canvas, new SKRect(x, row.Top, x + style.SpaceWidth, bottom), back);
        }
    }

    private void PaintText(SKCanvas canvas, PaintState state, ViewStyle style, Row row, int selStart, int selEnd)
    {
        SKColor? selectionFore = style.Elements.TryGetValue(state.Focused ? Element.SelectionText : Element.SelectionInactiveText, out var f)
            ? f : null;
        int selFrom = selStart - row.LineStart, selTo = selEnd - row.LineStart;
        var layout = row.Layout;
        foreach (var (start, end, s) in StyleRuns(layout, row.Start, row.End))
        {
            var fore = style.Styles[s].Fore;
            if (selectionFore is { } sf && selFrom < selTo && selFrom < end && selTo > start)
            {
                int a = Math.Max(start, selFrom), b = Math.Min(end, selTo);
                DrawPiece(canvas, style, row, start, a, fore);
                DrawPiece(canvas, style, row, a, b, sf);
                DrawPiece(canvas, style, row, b, end, fore);
            }
            else
            {
                DrawPiece(canvas, style, row, start, end, fore);
            }
        }
    }

    // Draws [start, end) of the line: shaped text, and representation blobs (rounded box in the text colour
    // with the mnemonic in the background colour) for items with a fixed width.
    private void DrawPiece(SKCanvas canvas, ViewStyle style, Row row, int start, int end, SKColor fore)
    {
        if (start >= end) return;
        row.Shape.Draw(canvas, start - row.Start, end - row.Start, row.TextLeft, row.Baseline, fore);
        foreach (var item in row.Layout.Items)
        {
            if (item.Width <= 0 || item.Start < start || item.Start >= end) continue;
            foreach (var interval in row.Shape.RangeIntervals(item.Start - row.Start, item.End - row.Start))
            {
                var box = new SKRect(row.TextLeft + interval.Left + 1, row.Top + 1, row.TextLeft + interval.Right - 1, row.Top + style.LineHeight - 1);
                paint.Color = fore;
                canvas.DrawRoundRect(box, 2, 2, paint);
                var mnemonic = Representation(row.Layout.Text.AsSpan(item.Start), out _) ?? "";
                shaper.DrawText(canvas, item.Font, Encoding.ASCII.GetBytes(mnemonic), box.Left + RepresentationPadding / 2 - 1, row.Baseline,
                    style.Styles[row.Layout.Styles[item.Start]].Back);
            }
        }
    }

    private void PaintIndicators(SKCanvas canvas, Document document, ViewStyle style, Row row)
    {
        int from = row.LineStart + row.Start, to = row.LineStart + row.End;
        if (from >= to) return;
        foreach (var decoration in document.Decorations.View)
        {
            if (decoration.Indicator >= ViewStyle.MaxIndicators) continue;
            var indicator = style.Indicators[decoration.Indicator];
            if (indicator.Style == IndicatorStyle.Hidden) continue;
            for (var position = from; position < to;)
            {
                var end = Math.Min(Math.Max(decoration.EndRun(position), position + 1), to);
                if (decoration.ValueAt(position) != 0)
                    foreach (var interval in row.Shape.RangeIntervals(position - row.LineStart - row.Start, end - row.LineStart - row.Start))
                        DrawIndicator(canvas, indicator, row.TextLeft + interval.Left, row.TextLeft + interval.Right, row.Baseline);
                position = end;
            }
        }
    }

    private void DrawIndicator(SKCanvas canvas, Indicator indicator, float left, float right, float baseline)
    {
        stroke.Color = indicator.Fore;
        var top = baseline + 1.5f;
        if (indicator.Style == IndicatorStyle.Squiggle)
        {
            // Zig-zag 2px tall with 2px steps, as upstream INDIC_SQUIGGLE.
            var x = left;
            var y = top;
            while (x < right)
            {
                var nextX = Math.Min(x + 2, right);
                var nextY = y == top ? top + 2 : top;
                canvas.DrawLine(x, y, nextX, nextY, stroke);
                (x, y) = (nextX, nextY);
            }
        }
        else
        {
            canvas.DrawLine(left, top, right, top, stroke);
        }
    }

    private void PaintMargins(SKCanvas canvas, Document document, ViewStyle style, SKRect client, List<(int Line, int Sub, float Top)> rows, float lineHeight)
    {
        var x = client.Left;
        var marginStyle = style.Styles[ViewStyle.LineNumber];
        foreach (var margin in style.Margins)
        {
            if (margin.Width <= 0) continue;
            var right = x + margin.Width;
            canvas.Save();
            canvas.ClipRect(new SKRect(x, client.Top, right, client.Bottom));
            Fill(canvas, new SKRect(x, client.Top, right, client.Bottom), marginStyle.Back);
            foreach (var (line, sub, top) in rows)
            {
                if (sub != 0) continue;
                var baseline = top + style.Ascent;
                if (margin.Type == MarginType.Number)
                {
                    var number = Encoding.ASCII.GetBytes((line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var font = marginStyle.FontSpec;
                    shaper.DrawText(canvas, font, number, right - MarginPadding - shaper.Measure(font, number), baseline, marginStyle.Fore);
                }
                else if (margin.Type is MarginType.RightText or MarginType.Text && document.MarginText.Text(line) is { } text)
                {
                    var textStyle = style.Styles[Math.Clamp(document.MarginText.Style(line), 0, ViewStyle.MaxStyles - 1)];
                    Fill(canvas, new SKRect(x, top, right, top + lineHeight), textStyle.Back);
                    var newline = text.IndexOf('\n', StringComparison.Ordinal);
                    var bytes = Encoding.UTF8.GetBytes(newline < 0 ? text : text[..newline]);
                    var font = textStyle.FontSpec;
                    var left = margin.Type == MarginType.RightText ? right - MarginPadding - shaper.Measure(font, bytes) : x;
                    shaper.DrawText(canvas, font, bytes, left, baseline, textStyle.Fore);
                }
            }
            canvas.Restore();
            x = right;
        }
    }

    // Maximal runs of equal style within [start, end) of the line.
    private static IEnumerable<(int Start, int End, byte Style)> StyleRuns(LineLayout layout, int start, int end)
    {
        for (var i = start; i < end;)
        {
            var s = layout.Styles[i];
            var j = i + 1;
            while (j < end && layout.Styles[j] == s) j++;
            yield return (i, j, s);
            i = j;
        }
    }

    private void Fill(SKCanvas canvas, SKRect rect, SKColor colour)
    {
        paint.Color = colour;
        canvas.DrawRect(rect, paint);
    }
}