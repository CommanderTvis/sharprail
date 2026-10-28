using System.Text;
using System.Text.Json;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

internal enum ChatHitKind { Toggle, Allow, Deny, Always, Rewind, Fork, OpenDiff, LoadFull, Link, EditInput, OpenThread, OpenText, Pick, OtherText, Submit, CancelAsk, Retry }

internal readonly record struct ChatHit(Rect Bounds, ChatHitKind Kind, string? Arg = null);

/// <summary>
/// One row laid out for a width: filled rectangles, cached <see cref="TextLayout"/>s and the click targets over them.
/// It exists only while a realized row draws; the row's height is remembered apart from it, so scrolling a row back
/// into view measures nothing.
/// </summary>
internal sealed class ChatRowLayout : IDisposable
{
    internal const double Pad = 12, Indent = 18, Gap = 6;
    private const int PreviewLines = 12;

    private readonly record struct Fill(Rect Bounds, IBrush Brush, double Radius, IPen? Pen);
    private readonly record struct Placed(Point Origin, TextLayout Text, bool HoverOnly, bool Selectable = true, Rect? Clip = null);

    /// <summary>One run of selectable text: its source string, and its layout and origin while the run is realized.</summary>
    internal readonly record struct Part(string Text, TextLayout? Layout, Point Origin, Rect? Clip = null);

    // The string each text layout was made from, so selection offsets are offsets into what the user reads.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextLayout, string> Sources = new();

    private readonly List<Fill> fills = [];
    // Drawn over the text: the fade at the foot of a clipped row.
    private readonly List<Fill> overlays = [];
    private readonly List<Placed> texts = [];
    private readonly List<ChatHit> hits = [];
    private readonly double left;
    private readonly double inner;
    private double y;
    // An assistant reply is a stack of blocks: each is measured once for its height and realized only while it is near the viewport.
    private readonly List<BlockPlan> plans = [];

    private sealed class BlockPlan(Markdown.Piece piece, double top, double height, bool caret, IReadOnlyList<string> texts)
    {
        public IReadOnlyList<string> Texts { get; } = texts;
        public Markdown.Piece Piece { get; } = piece;
        public double Top { get; } = top;
        public double Height { get; } = height;
        public bool Caret { get; } = caret;
        public ChatRowLayout? Fragment { get; set; }
    }

    public double Height => y;
    private double blocksBottom;

    /// <summary>The y where a reply's text ends and its action strip begins; zero for rows that have none.</summary>
    internal double BlocksBottom => blocksBottom;

    /// <summary>The bounds of the row's own text, hover-only action labels left out: what an action must not overlap.</summary>
    internal IReadOnlyList<Rect> TextBounds() => [.. texts.Where(text => !text.HoverOnly).Select(text => new Rect(text.Origin, new Size(text.Text.Width, text.Text.Height)))];

    /// <summary>The row's own click targets (those of realized blocks not included).</summary>
    public IReadOnlyList<ChatHit> OwnHits => hits;

    public int TextCount => texts.Count + plans.Sum(plan => plan.Fragment?.texts.Count ?? 0);

    /// <summary>The click target at a point of the row, if any.</summary>
    public ChatHit? HitAt(Point point)
    {
        foreach (var hit in hits)
            if (hit.Bounds.Contains(point)) return hit;
        foreach (var plan in plans)
            if (plan.Fragment is { } fragment && point.Y >= plan.Top && point.Y < plan.Top + plan.Height && fragment.HitAt(new Point(point.X, point.Y - plan.Top)) is { } found) return found with { Bounds = found.Bounds.Translate(new Vector(0, plan.Top)) };
        return null;
    }

    // ---- selection: the row's selectable text is its parts joined by a newline; an offset is a position in that string.

    private IEnumerable<Part> OwnParts() => texts.Where(text => text.Selectable).Select(text => new Part(Sources.TryGetValue(text.Text, out var source) ? source : "", text.Text, text.Origin, text.Clip));

    /// <summary>Every run of selectable text in reading order; runs of blocks that are not realized have no layout.</summary>
    internal IReadOnlyList<Part> Parts()
    {
        var list = new List<Part>();
        foreach (var plan in plans)
        {
            if (plan.Fragment is { } fragment) list.AddRange(fragment.OwnParts().Select(part => part with { Origin = new Point(part.Origin.X, part.Origin.Y + plan.Top), Clip = part.Clip?.Translate(new Vector(0, plan.Top)) }));
            else list.AddRange(plan.Texts.Select(text => new Part(text, null, default)));
        }
        list.AddRange(OwnParts());
        return list;
    }

    /// <summary>The row's selectable text.</summary>
    internal string SelectableText() => string.Join("\n", Parts().Select(part => part.Text));

    /// <summary>Realizes the blocks that meet the interval, as drawing would, so a point in it can be hit-tested.</summary>
    internal void Realize(double top, double bottom)
    {
        foreach (var plan in plans)
            if (plan.Fragment is null && plan.Top + plan.Height >= top && plan.Top <= bottom) plan.Fragment = Fragment(plan.Piece, plan.Caret);
    }

    /// <summary>The offset of the character nearest a point of the row, or -1 when no text is realized.</summary>
    internal int OffsetAt(Point point)
    {
        var parts = Parts();
        var start = 0;
        var best = -1;
        var bestDistance = double.MaxValue;
        foreach (var part in parts)
        {
            if (part.Layout is { } layout)
            {
                var distance = point.Y < part.Origin.Y ? part.Origin.Y - point.Y : point.Y > part.Origin.Y + layout.Height ? point.Y - part.Origin.Y - layout.Height : 0;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    var local = new Point(point.X - part.Origin.X, Math.Clamp(point.Y - part.Origin.Y, 0, Math.Max(0, layout.Height - 1)));
                    best = start + Math.Clamp(layout.HitTestPoint(local).TextPosition, 0, part.Text.Length);
                }
            }
            start += part.Text.Length + 1;
        }
        return best;
    }

    /// <summary>The rectangles, in the row's coordinates, that cover the realized characters from <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal List<Rect> RangeRects(int from, int to)
    {
        var rects = new List<Rect>();
        var start = 0;
        foreach (var part in Parts())
        {
            var s = Math.Max(from, start) - start;
            var e = Math.Min(to, start + part.Text.Length) - start;
            if (part.Layout is { } layout && e > s)
                foreach (var rect in layout.HitTestTextRange(s, e - s))
                {
                    // Only the glyphs: never past the end of the line's last glyph but a small newline cue, and never outside the clip the text is drawn in.
                    var line = LineBox(layout, rect.Y + rect.Height / 2);
                    var right = Math.Min(rect.Right, line.Right + NewlineCue);
                    if (right - rect.X <= 0) continue;
                    var box = new Rect(part.Origin.X + rect.X, part.Origin.Y + rect.Y, right - rect.X, rect.Height);
                    if (part.Clip is { } clip) box = box.Intersect(clip);
                    if (box.Width > 0 && box.Height > 0) rects.Add(box);
                }
            start += part.Text.Length + 1;
        }
        return rects;
    }

    internal const double NewlineCue = 4;

    /// <summary>The box of the line of <paramref name="layout"/> at a y, up to its last glyph (trailing blanks left out).</summary>
    internal static Rect LineBox(TextLayout layout, double y)
    {
        var top = 0.0;
        Rect last = default;
        foreach (var line in layout.TextLines)
        {
            last = new Rect(line.Start, top, line.Width, line.Height);
            if (y < top + line.Height) return last;
            top += line.Height;
        }
        return last;
    }

    /// <summary>The word around an offset of <paramref name="text"/>.</summary>
    internal static (int Start, int End) WordAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        var a = offset; var b = offset;
        if (offset < text.Length && Word(text[offset]) || offset > 0 && Word(text[offset - 1]))
        {
            while (a > 0 && Word(text[a - 1])) a--;
            while (b < text.Length && Word(text[b])) b++;
        }
        else if (offset < text.Length && text[offset] != '\n') b = offset + 1;
        return (a, b);
    }

    /// <summary>The paragraph (one run of text, or one line of a code block) around an offset of <paramref name="text"/>.</summary>
    internal static (int Start, int End) ParagraphAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var a = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var b = text.IndexOf('\n', offset);
        return (a, b < 0 ? text.Length : b);
    }

    /// <summary>The row's click targets in reading order, those of realized blocks included, for keyboard focus; whole-line toggles and (unless the row is hovered) the hover-only actions are left out.</summary>
    internal IReadOnlyList<ChatHit> TabStops(bool hovered)
    {
        var stops = new List<ChatHit>(hits);
        foreach (var plan in plans)
            if (plan.Fragment is { } fragment) stops.AddRange(fragment.hits.Select(hit => hit with { Bounds = hit.Bounds.Translate(new Vector(0, plan.Top)) }));
        return [.. stops.Where(hit => hit.Kind != ChatHitKind.Toggle && (hovered || hit.Kind is not (ChatHitKind.Fork or ChatHitKind.Rewind))).OrderBy(hit => Math.Round(hit.Bounds.Y)).ThenBy(hit => hit.Bounds.X)];
    }

    /// <summary>True when the row has blocks that come and go with the viewport, so scrolling must redraw it.</summary>
    public bool Blocked => plans.Count > 1;

    private ChatRowLayout(double left, double width)
    {
        this.left = left;
        inner = Math.Max(40, width - left - Pad);
    }

    public static ChatRowLayout Build(ChatRow row, double width, bool expandable)
    {
        var layout = new ChatRowLayout(Pad + row.Depth * Indent, width);
        // An empty row draws nothing and takes no room.
        if (IsEmpty(row.Item))
        {
            Interlocked.Increment(ref live);
            Interlocked.Increment(ref built);
            return layout;
        }
        layout.y = 4;
        switch (row.Item)
        {
            case ChatUserItem user: layout.User(user, row.Expanded); break;
            case ChatAssistantItem assistant: layout.Assistant(assistant); break;
            case ChatThinkingItem thinking: layout.Thinking(thinking, row.Expanded); break;
            case ChatActivityItem activity: layout.Activity(activity, row.Expanded); break;
            case ChatToolItem tool: layout.Tool(tool, row, expandable); break;
            case ChatNoticeItem notice: layout.Notice(notice, row.Expanded); break;
            case ChatQuestionItem question: layout.Question(question); break;
        }
        layout.y += 4;
        Interlocked.Increment(ref live);
        Interlocked.Increment(ref built);
        Interlocked.Add(ref liveTexts, layout.texts.Count);
        return layout;
    }

    /// <summary>A row with nothing to show: a reply without text, a prompt without text or attachments.</summary>
    internal static bool IsEmpty(ChatItem item) => item switch
    {
        ChatAssistantItem { Streaming: false } assistant => string.IsNullOrWhiteSpace(assistant.Text),
        ChatUserItem user => string.IsNullOrWhiteSpace(user.Text) && user.Attachments is not { Count: > 0 },
        // A request is answered in the panel above the composer; the tool's own card is what stays in the list.
        ChatPermissionItem => true,
        ChatToolItem tool when ToolCards.Hidden(tool.Name) => true,
        ChatQuestionItem { Decision: ChatPermissionDecision.Pending } => true,
        ChatNoticeItem notice => string.IsNullOrWhiteSpace(notice.Text) && string.IsNullOrWhiteSpace(notice.Label) && notice.Sections.Count == 0,
        _ => false
    };

    public void Draw(DrawingContext context, bool hovered, double top = double.NegativeInfinity, double bottom = double.PositiveInfinity, IReadOnlyList<Rect>? marks = null)
    {
        foreach (var plan in plans)
        {
            var near = plan.Top + plan.Height >= top && plan.Top <= bottom;
            if (!near)
            {
                if (plan.Fragment is { } far) { far.Dispose(); plan.Fragment = null; }
                continue;
            }
            plan.Fragment ??= Fragment(plan.Piece, plan.Caret);
            var inside = marks?.Where(mark => mark.Bottom > plan.Top && mark.Top < plan.Top + plan.Height).Select(mark => mark.Translate(new Vector(0, -plan.Top))).ToList();
            using (context.PushTransform(Matrix.CreateTranslation(0, plan.Top))) plan.Fragment.Draw(context, hovered, double.NegativeInfinity, double.PositiveInfinity, inside);
        }
        foreach (var fill in fills) context.DrawRectangle(fill.Brush, fill.Pen, fill.Bounds, fill.Radius, fill.Radius);
        DrawShapes(context);
        if (marks is not null)
            foreach (var mark in marks)
                if (!plans.Any(plan => mark.Bottom > plan.Top && mark.Top < plan.Top + plan.Height)) context.DrawRectangle(Ui.TextSelection, null, mark);
        foreach (var text in texts)
        {
            if (text.HoverOnly && !hovered) continue;
            if (text.Clip is { } clip)
                using (context.PushClip(clip)) text.Text.Draw(context, text.Origin);
            else text.Text.Draw(context, text.Origin);
        }
        foreach (var fade in overlays) context.DrawRectangle(fade.Brush, null, fade.Bounds);
    }

    public void Dispose()
    {
        foreach (var plan in plans) { plan.Fragment?.Dispose(); plan.Fragment = null; }
        foreach (var text in texts) text.Text.Dispose();
        Interlocked.Decrement(ref live);
        Interlocked.Add(ref liveTexts, -texts.Count);
        texts.Clear(); fills.Clear(); overlays.Clear(); hits.Clear(); geometries.Clear();
    }

    private static int live, liveTexts, built;

    /// <summary>Layouts built since start; streaming and scrolling are bounded by how few of these they cost.</summary>
    public static int Built => Volatile.Read(ref built);

    /// <summary>Layouts built and not yet disposed, and the text layouts they hold: what the checks bound.</summary>
    public static int Live => Volatile.Read(ref live);

    public static int LiveTexts => Volatile.Read(ref liveTexts);

    private static Typeface Face(bool bold = false, bool italic = false) =>
        new(Ui.InterfaceFont, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.SemiBold : Ui.InterfaceWeight);

    private static readonly Typeface Mono = new(Ui.CodeFont);

    private TextLayout Make(string text, IBrush brush, double size, double width, bool mono = false, bool bold = false, bool italic = false,
        int maxLines = 0, IReadOnlyList<ValueSpan<TextRunProperties>>? spans = null) =>
        Sourced(text, new(text, mono ? Mono : Face(bold, italic), size, brush, textWrapping: TextWrapping.Wrap, maxWidth: Math.Max(1, width),
            maxLines: maxLines, textTrimming: maxLines > 0 ? TextTrimming.CharacterEllipsis : null, textStyleOverrides: spans));

    private static TextLayout Sourced(string text, TextLayout layout)
    {
        Sources.AddOrUpdate(layout, text);
        return layout;
    }

    private Rect Place(TextLayout text, double x, double top, bool hoverOnly = false, bool selectable = true, Rect? clip = null)
    {
        texts.Add(new(new Point(x, top), text, hoverOnly, selectable && !hoverOnly, clip));
        return new Rect(x, top, text.Width, text.Height);
    }

    // A block of text at the running position; returns its bounds and advances.
    private Rect Block(string text, IBrush brush, double size, double indent = 0, bool mono = false, bool bold = false, bool italic = false,
        int maxLines = 0, IReadOnlyList<ValueSpan<TextRunProperties>>? spans = null)
    {
        var layout = Make(text.Length == 0 ? " " : text, brush, size, inner - indent, mono, bold, italic, maxLines, spans);
        var bounds = Place(layout, left + indent, y);
        y += layout.Height;
        return bounds;
    }

    private void Boxed(string text, IBrush brush, IBrush background, int maxLines = 0)
    {
        var layout = Make(text.Length == 0 ? " " : text, brush, 12, inner - 16, mono: true, maxLines: maxLines);
        fills.Add(new(new Rect(left, y, inner, layout.Height + 12), background, 4, null));
        Place(layout, left + 8, y + 6);
        y += layout.Height + 12 + 2;
    }

    // Chevron and status dot share one centre: the middle of the header's first line, taken from its laid-out text.
    private static double LineCentre(TextLayout text, double top) => top + text.Height / 2;

    /// <summary>The vertical centres of the first chevron, the first status dot and the first text line, for checks; null where the row has none.</summary>
    internal (double? Chevron, double? Dot, double? Text) GlyphCentres() =>
        (geometries.Count > 0 ? geometries[0].Shape.Bounds.Center.Y : null,
            fills.Where(fill => fill.Bounds.Width == 7 && fill.Radius == 3.5).Select(fill => (double?)fill.Bounds.Center.Y).FirstOrDefault(),
            texts.Count > 0 ? texts[0].Origin.Y + texts[0].Text.Height / 2 : null);

    private void Chevron(double mid, bool open)
    {
        var pen = new Pen(Ui.Muted, 1.3);
        var x = left;
        var stroke = new StreamGeometry();
        using (var context = stroke.Open())
        {
            if (open) { context.BeginFigure(new Point(x + 1, mid - 1.5), false); context.LineTo(new Point(x + 4, mid + 1.5)); context.LineTo(new Point(x + 7, mid - 1.5)); }
            else { context.BeginFigure(new Point(x + 2, mid - 3), false); context.LineTo(new Point(x + 5, mid)); context.LineTo(new Point(x + 2, mid + 3)); }
            context.EndFigure(false);
        }
        geometries.Add((stroke, pen));
    }

    internal readonly List<(Geometry Shape, IPen Pen)> geometries = [];

    internal void DrawShapes(DrawingContext context)
    {
        foreach (var (shape, pen) in geometries) context.DrawGeometry(null, pen, shape);
    }

    private const int UserClipLines = 3;

    // A human prompt is the one bubble: plain text, clipped to about 60 pixels with Show more and Show less. A prompt that did not come from
    // a human is a collapsed row instead, as the extension draws a message from a plugin, channel or the SDK.
    private void User(ChatUserItem user, bool expanded)
    {
        if (user.Origin is { Kind: not "human" } origin)
        {
            var who = origin.Name is { Length: > 0 } name ? "@" + name : origin.Kind;
            Disclosure("Message from " + who, Preview(user.Text), expanded, () => Block(user.Text, Ui.TextBrush, 12, indent: 14));
            return;
        }
        var whole = Make(user.Text.Length == 0 ? " " : user.Text, Ui.TextBrush, Ui.FontSize, inner - 20);
        var large = whole.Height > 64;
        var layout = large && !expanded ? Make(user.Text, Ui.TextBrush, Ui.FontSize, inner - 20, maxLines: UserClipLines) : whole;
        var extra = (user.Attachments is { Count: > 0 } ? 18 : 0) + (user.Queued ? 16 : 0) + (large ? 20 : 0);
        var box = new Rect(left, y, inner, layout.Height + 16 + extra);
        fills.Add(new(box, Ui.Elevated, 8, new Pen(Ui.BorderBrush)));
        Place(layout, left + 10, y + 8);
        var cursor = y + 8 + layout.Height;
        if (user.Attachments is { Count: > 0 } attachments)
        {
            Place(Make(string.Join(", ", attachments.Select(item => item.Name)), Ui.Muted, 12, inner - 20, maxLines: 1), left + 10, cursor + 2);
            cursor += 18;
        }
        if (user.Queued) { Place(Make("queued", Ui.Warning, 11, inner - 20, maxLines: 1), left + 10, cursor + 2, selectable: false); cursor += 16; }
        if (large)
        {
            var more = Make(expanded ? "Show less" : "Show more", Ui.Accent, 11, 120, maxLines: 1);
            var moreBox = Place(more, left + 10, cursor + 2, selectable: false);
            hits.Add(new(moreBox.Inflate(4), ChatHitKind.Toggle));
        }
        y = box.Bottom;
        if (user.RanAhead) Block("Ran at once, while the assistant was working.", Ui.Muted, 11, italic: true);
        // Fork and rewind sit in a strip below a human prompt that has been taken up, so they never cover its text.
        if (user.Uuid is not null && !user.Queued)
        {
            var fork = Make("Fork", Ui.Accent, 11, 80);
            var rewind = Make("Rewind", Ui.Accent, 11, 80);
            var forkBox = Place(fork, box.Right - 10 - fork.Width, y + 5, hoverOnly: true);
            var rewindBox = Place(rewind, forkBox.Left - 12 - rewind.Width, y + 5, hoverOnly: true);
            hits.Add(new(forkBox.Inflate(3), ChatHitKind.Fork));
            hits.Add(new(rewindBox.Inflate(3), ChatHitKind.Rewind));
            y += 24;
        }
    }

    /// <summary>The first non-blank line, whitespace collapsed, cut at 512 characters: a collapsed row's preview.</summary>
    internal static string Preview(string? text)
    {
        var line = (text ?? "").Split('\n').Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ");
        return line.Length > 512 ? line[..512] + "\u2026" : line;
    }

    // A row folded to one line with a chevron: its label, a colon and a preview, the body when opened.
    private void Disclosure(string label, string? preview, bool open, Action body, bool italic = false)
    {
        var text = preview is { Length: > 0 } ? label + ": " + preview : label;
        var head = Make(text, Ui.Muted, 12, inner - 20, italic: italic, maxLines: 1);
        Chevron(LineCentre(head, y + 3), open);
        Place(head, left + 14, y + 3);
        hits.Add(new(new Rect(left, y, inner, 22), ChatHitKind.Toggle));
        y += 24;
        if (open) { body(); y += 2; }
    }

    // Each block is laid out once to learn its height and let go; the ones near the viewport are laid out again when drawn.
    private void Assistant(ChatAssistantItem assistant)
    {
        var pieces = Markdown.Parse(assistant.Text);
        for (var index = 0; index < pieces.Count; index++)
        {
            var caret = assistant.Streaming && index == pieces.Count - 1;
            using var fragment = Fragment(pieces[index], caret);
            plans.Add(new BlockPlan(pieces[index], y, fragment.y, caret, [.. fragment.OwnParts().Select(part => part.Text)]));
            y += fragment.y;
        }
        if (assistant.Streaming && pieces.Count == 0)
        {
            fills.Add(new(new Rect(left, y + 2, 7, 14), Ui.Accent, 1, null));
            y += 22;
        }
        // A reply has no hover action: Copy is in the right-click menu.
        if (!assistant.Streaming) blocksBottom = y;
    }

    private ChatRowLayout Fragment(Markdown.Piece piece, bool caret)
    {
        var fragment = new ChatRowLayout(left, left + inner + Pad);
        fragment.Piece(piece, caret);
        Interlocked.Increment(ref live);
        Interlocked.Add(ref liveTexts, fragment.texts.Count);
        return fragment;
    }

    private void Piece(Markdown.Piece piece, bool caret)
    {
        switch (piece.Kind)
        {
            case Markdown.Kind.Code:
                Boxed(piece.Text, Ui.TextBrush, Ui.Hover, 0);
                y += Gap - 2;
                break;
            case Markdown.Kind.Rule:
                y += 6;
                fills.Add(new(new Rect(left, y, inner, 1), Ui.BorderBrush, 0, null));
                y += 9;
                break;
            case Markdown.Kind.Table:
                Table(piece);
                y += Gap;
                break;
            case Markdown.Kind.Item:
                var shift = piece.Level * Indent + 16;
                var marker = Make(piece.Marker, Ui.Muted, Ui.FontSize, 40, maxLines: 1);
                Place(marker, left + piece.Level * Indent, y, selectable: false);
                Rich(piece.Text, Ui.TextBrush, Ui.FontSize, shift, caret);
                y += 2;
                break;
            case Markdown.Kind.Quote:
                var top = y;
                Rich(piece.Text, Ui.Muted, Ui.FontSize, 12, caret);
                fills.Add(new(new Rect(left, top, 3, y - top), Ui.BorderBrush, 1, null));
                y += Gap;
                break;
            case Markdown.Kind.Heading:
                Rich(piece.Text, Ui.TextBrush, Ui.FontSize + Math.Max(0, 4 - piece.Level), 0, caret, bold: true);
                y += Gap;
                break;
            default:
                Rich(piece.Text, Ui.TextBrush, Ui.FontSize, 0, caret);
                y += Gap;
                break;
        }
    }

    // Inline Markdown as one layout: emphasis, code and links are style runs over it, and each link keeps the rectangles it covers as click targets.
    private void Rich(string markdown, IBrush ink, double size, double indent, bool caret, bool bold = false)
    {
        var (plain, spans, links) = Markdown.Inline(markdown, ink, Ui.Accent, size);
        var layout = Make(plain.Length == 0 ? " " : plain, ink, size, inner - indent, bold: bold, spans: spans);
        var origin = new Point(left + indent, y);
        texts.Add(new(origin, layout, false));
        foreach (var (start, length, url) in links)
            foreach (var rect in layout.HitTestTextRange(start, length))
                hits.Add(new(new Rect(origin.X + rect.X, origin.Y + rect.Y, rect.Width, rect.Height), ChatHitKind.Link, url));
        if (caret)
        {
            var end = layout.HitTestTextPosition(plain.Length);
            fills.Add(new(new Rect(origin.X + end.Right + 2, origin.Y + end.Y + 2, 7, Math.Max(10, end.Height - 4)), Ui.Accent, 1, null));
        }
        y += layout.Height;
    }

    private void Table(Markdown.Piece piece)
    {
        var rows = piece.Rows ?? [];
        var columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(row => row.Count));
        var cellWidth = inner / columns;
        var top = y;
        for (var r = 0; r < rows.Count; r++)
        {
            var layouts = new List<TextLayout>();
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";
                var (plain, spans, _) = Markdown.Inline(text, Ui.TextBrush, Ui.Accent, 12);
                layouts.Add(Make(plain.Length == 0 ? " " : plain, Ui.TextBrush, 12, cellWidth - 12, bold: r == 0, spans: spans));
            }
            var height = layouts.Max(layout => layout.Height) + 8;
            if (r == 0) fills.Add(new(new Rect(left, y, inner, height), Ui.Hover, 0, null));
            for (var c = 0; c < columns; c++) Place(layouts[c], left + c * cellWidth + 6, y + 4);
            y += height;
            fills.Add(new(new Rect(left, y - 1, inner, 1), Ui.BorderBrush, 0, null));
        }
        for (var c = 0; c <= columns; c++) fills.Add(new(new Rect(left + c * cellWidth, top, 1, y - top), Ui.BorderBrush, 0, null));
        fills.Add(new(new Rect(left, top, inner, 1), Ui.BorderBrush, 0, null));
    }

    // Upstream's activity breadcrumb: what the steps amounted to, then the steps themselves when opened.
    internal static string Breadcrumb(IReadOnlyList<ChatItem> steps)
    {
        var parts = new List<string>();
        void Count(string what, int count, string one, string many) { if (count > 0) parts.Add($"{what} {count} {(count == 1 ? one : many)}"); }
        Count("Read", steps.Count(step => step is ChatToolItem { Name: "Read" or "NotebookRead" }), "file", "files");
        Count("Searched", steps.Count(step => step is ChatToolItem { Name: "Grep" or "Glob" or "LS" }), "time", "times");
        Count("Fetched", steps.Count(step => step is ChatToolItem { Name: "WebFetch" or "WebSearch" }), "page", "pages");
        if (steps.Any(step => step is ChatThinkingItem)) parts.Add("Thought");
        return string.Join(" \u00B7 ", parts);
    }

    private void Activity(ChatActivityItem activity, bool open)
    {
        var live = activity.Steps.Any(step => step is ChatToolItem { State: ChatToolState.Running or ChatToolState.Pending } or ChatThinkingItem { Streaming: true });
        var tag = Make(live ? "Working\u2026" : "Done", live ? Ui.Accent : Ui.Success, 11, 120, maxLines: 1);
        var tagBox = Place(tag, left + inner - tag.Width - 2, y + 4);
        var crumb = Make(Breadcrumb(activity.Steps), Ui.Muted, 12, Math.Max(40, tagBox.Left - left - 24), maxLines: 1);
        Chevron(LineCentre(crumb, y + 3), open);
        Place(crumb, left + 14, y + 3);
        hits.Add(new(new Rect(left, y, inner, 22), ChatHitKind.Toggle));
        y += 24;
        if (!open) return;
        // Thinking with the tools it led to nested beneath it, as upstream's thinking group.
        var nest = 0;
        foreach (var step in activity.Steps)
        {
            var line = step switch
            {
                ChatToolItem tool => $"{tool.Name}  {tool.Summary}",
                ChatThinkingItem thinking => "Thinking  " + thinking.Text.ReplaceLineEndings(" "),
                _ => ""
            };
            if (step is ChatThinkingItem) nest = 0;
            Block(line, Ui.Muted, 11, indent: 14 + nest, mono: true, maxLines: 1);
            if (step is ChatThinkingItem) nest = 14;
        }
        y += 2;
    }

    private void Thinking(ChatThinkingItem thinking, bool open)
    {
        // Without text there is nothing to open: a static label, no chevron.
        var empty = string.IsNullOrWhiteSpace(thinking.Text);
        var head = Make(ToolCards.ThinkingLabel(thinking), Ui.Muted, 12, inner - 20, italic: true, maxLines: 1);
        if (!empty) Chevron(LineCentre(head, y + 2), open);
        var bounds = Place(head, left + (empty ? 0 : 14), y + 2);
        if (!empty) hits.Add(new(new Rect(left, y, inner, 22), ChatHitKind.Toggle));
        y += Math.Max(22, bounds.Height + 4);
        if (open && !empty) Block(thinking.Text, Ui.Muted, 12, indent: 14, italic: true);
    }

    private static string InputText(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Head(string text, int lines, out bool cut)
    {
        var index = -1;
        for (var count = 0; count < lines; count++)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0) { cut = false; return text; }
        }
        cut = true;
        return text[..index];
    }

    /// <summary>
    /// A review verdict read from a SharpRail review tool's output (the MCP <c>request_review</c> call), after upstream's RequestReviewCard:
    /// a one-line summary and the findings' comments. Null when the output is not a verdict, and the card is an ordinary tool card.
    /// </summary>
    internal static (string Summary, IReadOnlyList<string> Findings)? ReviewVerdict(string toolName, string? output)
    {
        if (!toolName.EndsWith("request_review", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object) root = details;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("verdict", out var verdict) || verdict.ValueKind != JsonValueKind.String) return null;
            var title = root.TryGetProperty("itemTitle", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : "the change set";
            var findings = root.TryGetProperty("findings", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(finding => finding.ValueKind == JsonValueKind.String ? finding.GetString() ?? "" :
                    finding.ValueKind == JsonValueKind.Object ? (new[] { "comment", "text", "message", "title" }.Select(name => finding.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null).FirstOrDefault(text => text is not null) ?? "") : "").Where(text => text.Length > 0).ToList()
                : [];
            var blocked = root.TryGetProperty("blockedByOpenFindings", out var open) && open.TryGetInt32(out var count) ? count : 0;
            var summary = verdict.GetString() == "approve"
                ? $"Approved \u201C{title}\u201D" + (blocked > 0 ? $" \u00B7 {blocked} still open" : "")
                : $"Changes requested on \u201C{title}\u201D \u00B7 {findings.Count} {(findings.Count == 1 ? "finding" : "findings")}";
            return (summary, findings);
        }
        catch (JsonException) { return null; }
    }

    // One IN or OUT row: a label, and the text in a bordered box clipped to 60 pixels with a fade; a long text opens in full on a click.
    private void Grid(string label, string text, bool mono, string which)
    {
        const double labelWidth = 30, clip = 60;
        var top = y;
        var shown = Head(text, 12, out _);
        if (shown.Length > 700) shown = shown[..700];
        var layout = Make(shown.Length == 0 ? " " : shown, Ui.TextBrush, 12, inner - labelWidth - 14 - 16, mono: mono);
        var visible = Math.Min(layout.Height, clip);
        var box = new Rect(left + 14 + labelWidth, top, inner - 14 - labelWidth, visible + 10);
        fills.Add(new(box, Ui.Elevated, 4, new Pen(Ui.BorderBrush, 0.5)));
        Place(Make(label, Ui.Hint, 11, labelWidth, mono: true, maxLines: 1), left + 14, top + 5, selectable: false);
        Place(layout, box.X + 8, top + 5, clip: new Rect(box.X, top + 5, box.Width, visible));
        // Text clipped away is neither highlighted, selectable nor copied: the layout's source is the part that shows.
        var shownChars = 0;
        var lineTop = 0.0;
        foreach (var line in layout.TextLines)
        {
            if (lineTop >= visible) break;
            shownChars += line.Length;
            lineTop += line.Height;
        }
        if (shownChars < shown.Length) Sources.AddOrUpdate(layout, shown[..shownChars]);
        if (layout.Height > clip && Ui.Elevated is ISolidColorBrush solid)
        {
            var color = solid.Color;
            var fade = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0), new GradientStop(color, 1)]
            };
            overlays.Add(new(new Rect(box.X + 1, box.Bottom - 5 - 11, box.Width - 2, 11), fade, 0, null));
        }
        if (ToolCards.Openable(text)) hits.Add(new(box, ChatHitKind.OpenText, which));
        y = box.Bottom + 4;
    }

    private void Tool(ChatToolItem tool, ChatRow row, bool expandable)
    {
        var review = ReviewVerdict(tool.Name, tool.Output);
        var checklist = tool.Name == "TodoWrite";
        var plan = tool.Name == "ExitPlanMode";
        var open = row.Expanded || checklist || plan;
        var dot = ToolCards.StatusOf(tool) switch
        {
            ToolCards.Dot.Failure => Ui.Danger,
            ToolCards.Dot.Denied => Ui.Warning,
            ToolCards.Dot.Success => Ui.Success,
            _ => Ui.Accent
        };
        var head = ToolCards.Header(tool);
        var name = Make(head.Name, Ui.TextBrush, 12, 220, bold: true, maxLines: 1);
        var centre = LineCentre(name, y + 3);
        Chevron(centre, open);
        fills.Add(new(new Rect(left + 14, centre - 3.5, 7, 7), dot, 3.5, null));
        var nameBox = Place(name, left + 28, y + 3);
        var right = left + inner - 2;
        // A subagent's thread opens in its own dialog, live.
        if (tool.Name is "Task" or "Agent")
        {
            var thread = Make("Open thread", Ui.Accent, 11, 100, maxLines: 1);
            var threadBox = Place(thread, right - thread.Width, y + 4);
            hits.Add(new(threadBox.Inflate(3), ChatHitKind.OpenThread, tool.Id));
            right = threadBox.Left - 12;
        }
        var summary = Make(review?.Summary ?? head.Secondary, Ui.Muted, 12, Math.Max(20, right - nameBox.Right - 8), mono: true, maxLines: 1);
        Place(summary, nameBox.Right + 8, y + 3);
        hits.Add(new(new Rect(left, y, inner, 22), ChatHitKind.Toggle));
        y += 24;
        if (ToolCards.Secondary(tool) is { Length: > 0 } line)
        {
            var lineBox = Block(line, Ui.Muted, 11, indent: 28);
            if (tool.Name is "Grep" or "Glob" or "Search" && !string.IsNullOrEmpty(tool.Output)) hits.Add(new(lineBox, ChatHitKind.OpenText, "out"));
            y += 2;
        }
        if (!open) return;
        var input = tool.Input;
        switch (tool.Name)
        {
            case "ExitPlanMode":
                PlanBody(InputText(input, "plan"));
                break;
            case "Skill":
                // The skill's name stays in the header; the request after it reads as ordinary text.
                if (InputText(input, "args") is { Length: > 0 } request) Block(request, Ui.TextBrush, 12, indent: 14);
                break;
            case "Edit" or "MultiEdit" or "Write" or "NotebookEdit":
                var path = tool.FilePath ?? InputText(input, "file_path");
                if (path.Length > 0)
                {
                    var link = Make("Open diff: " + path, Ui.Accent, 12, inner - 8, maxLines: 1);
                    var linkBox = Place(link, left + 4, y);
                    hits.Add(new(linkBox.Inflate(2), ChatHitKind.OpenDiff));
                    y += link.Height + 4;
                }
                var oldText = InputText(input, "old_string");
                var newText = tool.Name == "Write" ? InputText(input, "content") : tool.Name == "NotebookEdit" ? InputText(input, "new_source") : InputText(input, "new_string");
                if (oldText.Length > 0) Boxed(Prefixed("- ", Head(oldText, PreviewLines, out _)), Ui.Danger, Ui.DangerWash);
                if (newText.Length > 0) Boxed(Prefixed("+ ", Head(newText, PreviewLines, out _)), Ui.Success, Ui.SuccessWash);
                break;
            case "TodoWrite":
                Checklist(input);
                break;
        }
        var rows = ToolCards.Rows(tool);
        if (rows.In && InTextOf(tool) is { Length: > 0 } inText) Grid("IN", inText, tool.Name is "Bash" or "PowerShell" || tool.Input.ValueKind == JsonValueKind.Object, "in");
        var output = row.Full ?? tool.Output;
        if (review is { Findings.Count: > 0 } verdictCard)
        {
            foreach (var finding in verdictCard.Findings) Block("\u2022 " + finding, Ui.TextBrush, 12, indent: 14);
            output = null;
        }
        if (!string.IsNullOrEmpty(output) && (rows.Out || tool.State == ChatToolState.Failed))
            Grid("OUT", output, true, "out");
        if (tool.OutputTruncated && row.Full is null)
        {
            var load = Make("Load full output", Ui.Accent, 12, 200, maxLines: 1);
            var box = Place(load, left + 4, y);
            hits.Add(new(box.Inflate(2), ChatHitKind.LoadFull));
            y += load.Height + 4;
        }
    }

    private static string InTextOf(ChatToolItem tool) => ToolCards.InText(tool);

    /// <summary>The input field a user may change before allowing the call, by tool; null when the tool has none.</summary>
    internal static string? Editable(ChatPermissionItem permission) => permission.Input.ValueKind != JsonValueKind.Object ? null : permission.ToolName switch
    {
        "Bash" => "command",
        "Edit" => "new_string",
        "Write" => "content",
        "WebFetch" => "url",
        "Grep" or "Glob" => "pattern",
        _ => null
    } is { } field && permission.Input.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? field : null;

    private static string Prefixed(string prefix, string text) => prefix + text.Replace("\n", "\n" + prefix);

    private void Checklist(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return;
        foreach (var todo in todos.EnumerateArray())
        {
            var status = InputText(todo, "status");
            var content = InputText(todo, status == "in_progress" && InputText(todo, "activeForm").Length > 0 ? "activeForm" : "content");
            var mark = status switch { "completed" => "☑", "in_progress" => "◐", _ => "☐" };
            var done = status == "completed";
            IReadOnlyList<ValueSpan<TextRunProperties>>? spans = done ? [new ValueSpan<TextRunProperties>(2, content.Length, new GenericTextRunProperties(Face(), 12, TextDecorations.Strikethrough, Ui.Muted))] : null;
            Block(mark + " " + content, done ? Ui.Muted : Ui.TextBrush, 12, indent: 14, spans: spans);
        }
        y += 2;
    }

    // The plan Claude proposes, read as Markdown inside a tinted card.
    private void PlanBody(string plan)
    {
        var top = y;
        y += 6;
        foreach (var piece in Markdown.Parse(plan.Length == 0 ? "(no plan text)" : plan)) Piece(piece, false);
        fills.Insert(0, new(new Rect(left, top, inner, y - top), Ui.InfoWash, 6, new Pen(Ui.BorderBrush)));
    }

    // Notices by category, after the extension: interrupts and meta lines are italic, a task notification is its summary, a peer message or a
    // wrapped notice is a folded row, a command is its record, a compaction a folded row, and a failure a card with Try again.
    private void Notice(ChatNoticeItem notice, bool open)
    {
        switch (notice.Category)
        {
            case ChatNoticeKinds.Interrupt or ChatNoticeKinds.Meta or ChatNoticeKinds.Skipped or ChatNoticeKinds.RefusalFallback:
                Block(notice.Text, Ui.Muted, 12, italic: notice.Category != ChatNoticeKinds.Skipped);
                return;
            case ChatNoticeKinds.TaskNotification:
                if (notice.Sections.Count == 0) Block(notice.Text, Ui.Muted, 12, italic: true);
                else Disclosure(notice.Text, null, open, () => Sections(notice), italic: true);
                return;
            case ChatNoticeKinds.Peer:
                Disclosure(notice.Label is { Length: > 0 } lead ? lead : "Sender not recorded", Preview(notice.Detail ?? notice.Text), open, () => Block(notice.Detail ?? notice.Text, Ui.TextBrush, 12, indent: 14));
                return;
            case ChatNoticeKinds.Wrapped:
                Disclosure(notice.Label ?? notice.Text, null, open, () => Block(notice.Detail ?? notice.Text, Ui.TextBrush, 12, indent: 14));
                return;
            case ChatNoticeKinds.BashOutput:
                Disclosure(notice.Label ?? "Terminal command output", null, open, () => Sections(notice));
                return;
            case ChatNoticeKinds.Command:
                if (notice.Sections.Count == 0) Block(notice.Text, Ui.TextBrush, 12, mono: true);
                else Disclosure(notice.Text, null, open, () => Sections(notice));
                return;
            case ChatNoticeKinds.CommandResult:
                if (notice.Detail is null && notice.Level != ChatNoticeLevel.Error)
                {
                    var chip = Make(notice.Text, Ui.Muted, 12, inner - 20, italic: true, maxLines: 1);
                    fills.Add(new(new Rect(left, y, Math.Min(inner, chip.Width + 16), chip.Height + 6), Ui.Hover, 4, null));
                    Place(chip, left + 8, y + 3);
                    y += chip.Height + 8;
                }
                else Boxed(notice.Detail ?? notice.Text, notice.Level == ChatNoticeLevel.Error ? Ui.Danger : Ui.TextBrush, notice.Level == ChatNoticeLevel.Error ? Ui.DangerWash : Ui.Hover);
                return;
            case ChatNoticeKinds.Diagnostics:
                Block("\u26A0 " + notice.Text, Ui.Warning, 12);
                foreach (var section in notice.Sections) Block(section.Label + (section.Text.Length > 0 ? ": " + section.Text : ""), Ui.Muted, 11, indent: 14, mono: true);
                return;
            case ChatNoticeKinds.Compaction:
                Disclosure(notice.Text.StartsWith("Compacted", StringComparison.Ordinal) ? notice.Text : "Compacted chat \u00B7 " + notice.Text, null, open,
                    () => Block(notice.Detail ?? "Conversation was compacted to free up context.", Ui.Muted, 12, indent: 14));
                return;
            case ChatNoticeKinds.Failure:
                var top = y;
                y += 6;
                Block(notice.Text, Ui.Danger, 12, indent: 10);
                if (notice.Subtype is { Length: > 0 } subtype) Block(subtype, Ui.Muted, 11, indent: 10, mono: true);
                if (notice.Retryable == true)
                {
                    var label = Make("Try again", Ui.TextBrush, 12, 120, maxLines: 1, bold: true);
                    var box = new Rect(left + 10, y + 2, label.Width + 20, label.Height + 10);
                    fills.Add(new(box, Ui.Elevated, 4, new Pen(Ui.BorderBrush)));
                    Place(label, box.X + 10, box.Y + 5);
                    hits.Add(new(box, ChatHitKind.Retry));
                    y = box.Bottom;
                }
                y += 6;
                fills.Insert(0, new(new Rect(left, top, inner, y - top), Ui.DangerWash, 6, new Pen(Ui.Danger)));
                return;
            case ChatNoticeKinds.Retry:
                Block("\u21BB " + notice.Text, Ui.Warning, 12, italic: true);
                return;
            case ChatNoticeKinds.Restart:
                Block("\u21BA " + notice.Text, Ui.Muted, 12, italic: true);
                return;
        }
        Block(notice.Text, notice.Level switch { ChatNoticeLevel.Error => Ui.Danger, ChatNoticeLevel.Warning => Ui.Warning, _ => Ui.Muted }, 12, italic: notice.Level == ChatNoticeLevel.Info);
    }

    private void Sections(ChatNoticeItem notice)
    {
        foreach (var section in notice.Sections)
        {
            Block(section.Label, Ui.Hint, 11, indent: 14, bold: true);
            Block(section.Text, Ui.TextBrush, 12, indent: 14, mono: true);
        }
    }

    // A settled AskUserQuestion is a status line, as the extension's answered card: Answered with the count and the answers, No answer, Declined or
    // Session ended before an answer. A pending one is shown above the composer, not here.
    private void Question(ChatQuestionItem item)
    {
        switch (item.Outcome ?? (item.Decision == ChatPermissionDecision.Allowed ? "answered" : "declined"))
        {
            case "answered":
                var count = item.Questions.Count;
                Block($"Answered \u00B7 {count} {(count == 1 ? "question" : "questions")}", Ui.Success, 12, bold: true);
                foreach (var question in item.Questions)
                    Block($"{question.Question} \u2192 {(item.Answers is { } answers && answers.TryGetValue(question.Question, out var answer) ? answer : "")}", Ui.Muted, 12, indent: 14);
                break;
            case "no-answer": Block("No answer", Ui.Muted, 12, italic: true); break;
            case "session-ended": Block("Session ended before an answer", Ui.Muted, 12, italic: true); break;
            default:
                Block("Declined", Ui.Warning, 12, bold: true);
                if (item.Reason is { Length: > 0 } reason) Block(reason, Ui.Muted, 12, indent: 14);
                break;
        }
    }
}

/// <summary>
/// A small Markdown reader for drawn rows (a reply is read here rather than through the kit preview, whose control tree
/// per block would break bounded realization): fenced code, headings, nested and ordered lists, block quotes, rules,
/// tables, and inline emphasis, code, strikethrough and links. A link is clickable only when <see cref="SafeLink"/> says so.
/// </summary>
internal static class Markdown
{
    internal enum Kind { Paragraph, Heading, Code, Item, Quote, Rule, Table }

    internal sealed record Piece(Kind Kind, string Text, int Level = 0, string Marker = "", IReadOnlyList<IReadOnlyList<string>>? Rows = null);

    private static readonly System.Text.RegularExpressions.Regex ItemLine = new(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$");
    private static readonly System.Text.RegularExpressions.Regex RuleLine = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    private static readonly System.Text.RegularExpressions.Regex TableSeparator = new(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$");

    private static IReadOnlyList<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];
        return [.. text.Split('|').Select(cell => cell.Trim())];
    }

    public static IReadOnlyList<Piece> Parse(string text)
    {
        var pieces = new List<Piece>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var fence = "";
        var lines = text.Replace("\r", "").Split('\n');
        void Flush()
        {
            if (paragraph.Length > 0) pieces.Add(new(Kind.Paragraph, paragraph.ToString()));
            paragraph.Clear();
        }
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (fence.Length > 0)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal)) { pieces.Add(new(Kind.Code, code.ToString().TrimEnd('\n'))); code.Clear(); fence = ""; }
                else code.Append(line).Append('\n');
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                fence = trimmed[..3];
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            var level = trimmed.TakeWhile(c => c == '#').Count();
            if (level is > 0 and <= 6 && trimmed.Length > level && trimmed[level] == ' ')
            {
                Flush();
                pieces.Add(new(Kind.Heading, trimmed[(level + 1)..].Trim(), level));
                continue;
            }
            if (RuleLine.IsMatch(line)) { Flush(); pieces.Add(new(Kind.Rule, "")); continue; }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    var inner = lines[index].TrimStart()[1..];
                    quote.Append(inner.StartsWith(' ') ? inner[1..] : inner).Append('\n');
                    index++;
                }
                index--;
                pieces.Add(new(Kind.Quote, quote.ToString().TrimEnd('\n')));
                continue;
            }
            if (line.Contains('|') && index + 1 < lines.Length && TableSeparator.IsMatch(lines[index + 1]) && lines[index + 1].Contains('-'))
            {
                Flush();
                var rows = new List<IReadOnlyList<string>> { Cells(line) };
                index += 2;
                while (index < lines.Length && lines[index].Contains('|') && lines[index].Trim().Length > 0) rows.Add(Cells(lines[index++]));
                index--;
                pieces.Add(new(Kind.Table, "", Rows: rows));
                continue;
            }
            if (ItemLine.Match(line) is { Success: true } item)
            {
                Flush();
                var depth = Math.Min(4, item.Groups[1].Value.Replace("\t", "    ").Length / 2);
                var marker = item.Groups[2].Value;
                pieces.Add(new(Kind.Item, item.Groups[3].Value, depth, char.IsDigit(marker[0]) ? marker : depth switch { 0 => "\u2022", 1 => "\u25E6", _ => "\u25AA" }));
                continue;
            }
            // A line indented under an item continues it.
            if (paragraph.Length == 0 && pieces.Count > 0 && pieces[^1].Kind == Kind.Item && line.StartsWith("  ", StringComparison.Ordinal))
            {
                pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + "\n" + trimmed };
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(line);
        }
        if (fence.Length > 0 && code.Length > 0) pieces.Add(new(Kind.Code, code.ToString().TrimEnd('\n')));
        Flush();
        return pieces;
    }

    private static readonly System.Text.RegularExpressions.Regex FilePattern =
        new(@"^(?<path>[\w@./\\-]+\.[A-Za-z0-9]{1,8})(?:(?::|#L)(?<line>\d+)(?::(?<col>\d+))?)?$");

    /// <summary>
    /// A worktree-relative file reference, <c>path[:line[:col]]</c> or <c>path#Lline</c>: no scheme, no parent segments, no absolute or
    /// protocol-relative form (an absolute path inside the chat's directory is made relative by the view). Percent-encoding is decoded once first.
    /// </summary>
    public static (string Path, int Line, int Column)? FileTarget(string text)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(text.Trim()); }
        catch (UriFormatException) { return null; }
        if (decoded.StartsWith("//", StringComparison.Ordinal) || decoded.Contains("://", StringComparison.Ordinal) || decoded.StartsWith('#')) return null;
        if (FilePattern.Match(decoded) is not { Success: true } match) return null;
        var path = match.Groups["path"].Value.Replace('\\', '/');
        if (path.Split('/').Contains("..")) return null;
        return (path, match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0, match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 0);
    }

    /// <summary>Whether a link may be followed: web and mail links only; anything else (file:, javascript:, custom schemes) is shown as text.</summary>
    public static bool SafeLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    public static (string Plain, IReadOnlyList<ValueSpan<TextRunProperties>> Spans, IReadOnlyList<(int Start, int Length, string Url)> Links) Inline(
        string text, IBrush ink, IBrush accent, double size)
    {
        var plain = new StringBuilder();
        var spans = new List<ValueSpan<TextRunProperties>>();
        var links = new List<(int, int, string)>();
        void Run(string inner, Typeface face, double em, IBrush brush, TextDecorationCollection? decorations = null)
        {
            if (inner.Length == 0) return;
            spans.Add(new(plain.Length, inner.Length, new GenericTextRunProperties(face, em, decorations, brush)));
            plain.Append(inner);
        }
        var regular = new Typeface(Ui.InterfaceFont);
        var index = 0;
        while (index < text.Length)
        {
            var rest = text.AsSpan(index);
            if (text[index] == '`' && text.IndexOf('`', index + 1) is var end and > 0)
            {
                var code = text[(index + 1)..end];
                // A code span that names a file is a chip that opens it.
                if (FileTarget(code) is not null) links.Add((plain.Length, code.Length, "file:" + code));
                Run(code, new Typeface(Ui.CodeFont), size - 1, accent, FileTarget(code) is null ? null : new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } });
                index = end + 1;
            }
            else if ((rest.StartsWith("**") || rest.StartsWith("__")) && text.IndexOf(text.Substring(index, 2), index + 2, StringComparison.Ordinal) is var close and > 0)
            {
                Run(text[(index + 2)..close], new Typeface(Ui.InterfaceFont, FontStyle.Normal, FontWeight.Bold), size, ink);
                index = close + 2;
            }
            else if (rest.StartsWith("~~") && text.IndexOf("~~", index + 2, StringComparison.Ordinal) is var strike and > 0)
            {
                Run(text[(index + 2)..strike], regular, size, ink, new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Strikethrough } });
                index = strike + 2;
            }
            else if ((text[index] == '*' || text[index] == '_') && index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]) &&
                (text[index] == '*' || index == 0 || !char.IsLetterOrDigit(text[index - 1])) &&
                text.IndexOf(text[index], index + 1) is var em and > 0 && !char.IsWhiteSpace(text[em - 1]))
            {
                Run(text[(index + 1)..em], new Typeface(Ui.InterfaceFont, FontStyle.Italic, Ui.InterfaceWeight), size, ink);
                index = em + 1;
            }
            else if (text[index] == '[' && text.IndexOf("](", index + 1, StringComparison.Ordinal) is var mid and > 0 && text.IndexOf(')', mid + 2) is var stop and > 0)
            {
                var label = text[(index + 1)..mid];
                var url = text[(mid + 2)..stop].Trim();
                if (SafeLink(url))
                {
                    links.Add((plain.Length, label.Length, url));
                    Run(label, regular, size, accent, new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } });
                }
                else if (FileTarget(url) is not null)
                {
                    links.Add((plain.Length, label.Length, "file:" + url));
                    Run(label, regular, size, accent, new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } });
                }
                else plain.Append(label);
                index = stop + 1;
            }
            else if ((rest.StartsWith("http://") || rest.StartsWith("https://")) && (index == 0 || !char.IsLetterOrDigit(text[index - 1])))
            {
                var stopAt = index;
                while (stopAt < text.Length && !char.IsWhiteSpace(text[stopAt]) && text[stopAt] != ')' && text[stopAt] != '>') stopAt++;
                var url = text[index..stopAt].TrimEnd('.', ',', ';', ':', '!', '?');
                links.Add((plain.Length, url.Length, url));
                Run(url, regular, size, accent, new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } });
                index += url.Length;
            }
            else plain.Append(text[index++]);
        }
        return (plain.ToString(), spans, links);
    }
}