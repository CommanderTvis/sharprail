using System.Diagnostics;
using System.Text;

using SharpRail.Scintilla.Core;
using SharpRail.Scintilla.View;

using SkiaSharp;

namespace SharpRail.Scintilla.Editing;

/// <summary>Keys the editor handles (Scintilla SCK_* values, as ScintillaEditor.Input sends).</summary>
internal enum EditorKey { Escape = 7, Back = 8, Tab = 9, Return = 13, Down = 300, Up = 301, Left = 302, Right = 303, Home = 304, End = 305, Prior = 306, Next = 307, Delete = 308, Insert = 309 }

[Flags]
internal enum EditorModifiers { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

internal enum MouseAction { Down = 0, Move = 1, Up = 2 }

/// <summary>
/// The managed editor model replacing the native SkiaEditor (Native/Editor.cxx) and upstream Editor/EditModel:
/// numeric messages through <see cref="Send"/>, text-carrying messages through typed methods, input, idle wrapping
/// and painting onto a canvas. UI thread only; never throws across ScintillaDocument for bad input it can clamp.
/// </summary>
/// <remarks>Single stream selection only. Notifications, clipboard, IME, autocompletion, call tips, lexers, folding,
/// markers, annotations, multiple and rectangular selection, printing, change history and search are not ported.</remarks>
internal sealed class Editor : IDocumentWatcher, IDisposable
{
    private enum SelectionUnit { Character, Word, Line }

    private const uint DoubleClickTime = 500;
    private const float DoubleClickClose = 3;
    private const int CaretSlop = 50;

    private readonly Document doc = new();
    private readonly Selection sel = new();
    private readonly ContractionState cs = new();
    private readonly ViewStyle vs = new();
    private readonly LineShaper shaper;
    private readonly EditView view;

    private long revision;
    private SKRect client = new(0, 0, 800, 600);
    private int topLine;
    private float xOffset;
    private int scrollWidth = 2000;
    private bool scrollWidthTracking;
    private bool focused;
    private bool caretOn = true;
    private bool overtype;
    private float xChosen = -1;
    private int wrapPending = int.MaxValue; // First document line whose wrap height may be stale.

    // Mouse state.
    private bool captured;
    private SelectionUnit unit;
    private uint lastClickTime;
    private SKPoint lastClick = new(-100, -100), lastPointer;
    private int originalStart, originalEnd; // Word or line range of the initial click.

    public Editor(SKTypeface typeface)
    {
        shaper = new LineShaper(typeface);
        view = new EditView(shaper);
        doc.AddWatcher(this);
        RefreshStyle();
    }

    /// <summary>Incremented on every document change (the native NotifyChange revision).</summary>
    public long Revision => revision;

    /// <summary>Set by every invalidation; ScintillaEditor clears it after recording.</summary>
    public bool Dirty { get; set; } = true;

    internal Document Document => doc;

    internal Selection Selection => sel;

    internal ViewStyle ViewStyle => vs;

    private bool Wrapping => vs.WrapWord;

    private SKRect TextRect => new(client.Left + vs.FixedColumnWidth, client.Top, client.Right - vs.RightMarginWidth, client.Bottom);

    private float WrapWidth => Wrapping ? Math.Max(1, TextRect.Width) : float.PositiveInfinity;

    private int LinesOnScreen => vs.LineHeight > 0 ? Math.Max(1, (int)(client.Height / vs.LineHeight)) : 1;

    private int MaxScrollPos => Math.Max(0, cs.LinesDisplayed - LinesOnScreen);

    private int Caret => sel.MainCaret.Position;

    private int Anchor => sel.MainAnchor.Position;

    private void Redraw() => Dirty = true;

    // Messages

    /// <summary>Numeric messages listed in ScintillaMessage; unknown messages return 0.</summary>
    public nint Send(ScintillaMessage message, nint w = 0, nint l = 0)
    {
        switch (message)
        {
            // Text and document
            case ScintillaMessage.AddText:
            case ScintillaMessage.ReplaceSel:
            case ScintillaMessage.GetTextRangeFull:
            case ScintillaMessage.SetStylingEx:
                return Pointer(message, l);
            case ScintillaMessage.GetText:
                return l == 0 ? doc.Length : Pointer(message, l);
            case ScintillaMessage.GetSelText:
                return l == 0 ? sel.Length : Pointer(message, l);
            case ScintillaMessage.ClearAll:
                ClearAll();
                break;
            case ScintillaMessage.GetLength:
                return doc.Length;
            case ScintillaMessage.Clear:
                Clear();
                break;
            case ScintillaMessage.DeleteRange:
                doc.DeleteChars(ClampPosition(w), (int)Math.Clamp(l, 0, doc.Length));
                break;
            case ScintillaMessage.SetEOLMode:
                if (w is >= 0 and <= 2) doc.EolMode = (EndOfLine)w;
                break;
            case ScintillaMessage.SetReadOnly:
                doc.ReadOnly = w != 0;
                break;
            case ScintillaMessage.GetReadOnly:
                return doc.ReadOnly ? 1 : 0;
            case ScintillaMessage.SetSavePoint:
                doc.SetSavePoint();
                break;
            case ScintillaMessage.GetModify:
                return doc.IsModified ? 1 : 0;
            case ScintillaMessage.EmptyUndoBuffer:
                doc.DeleteUndoHistory();
                break;
            case ScintillaMessage.Undo:
                Undo();
                break;
            case ScintillaMessage.Redo:
                Redo();
                break;

            // Lines and words
            case ScintillaMessage.GetLineCount:
                return doc.LinesTotal;
            case ScintillaMessage.LineFromPosition:
                return doc.LineFromPosition(ClampPosition(w));
            case ScintillaMessage.PositionFromLine:
                if (w < 0) return doc.LineStart(doc.LineFromPosition(Caret));
                return w > doc.LinesTotal ? -1 : doc.LineStart((int)w);
            case ScintillaMessage.GetLineEndPosition:
                return w < 0 || w >= doc.LinesTotal ? (w < 0 ? 0 : doc.Length) : doc.LineEnd((int)w);
            case ScintillaMessage.WordStartPosition:
                return doc.WordStartPosition(ClampPosition(w), l != 0);
            case ScintillaMessage.WordEndPosition:
                return doc.WordEndPosition(ClampPosition(w), l != 0);

            // Selection and caret
            case ScintillaMessage.GetCurrentPos:
                return Caret;
            case ScintillaMessage.GetAnchor:
                return Anchor;
            case ScintillaMessage.GetSelectionStart:
                return sel.Start.Position;
            case ScintillaMessage.GetSelectionEnd:
                return sel.End.Position;
            case ScintillaMessage.SetSel:
                {
                    var end = l < 0 ? doc.Length : ClampPosition(l);
                    var start = w < 0 ? end : ClampPosition(w);
                    SetSelection(end, start);
                    EnsureCaretVisible();
                    break;
                }
            case ScintillaMessage.SetEmptySelection:
                SetEmptySelection(ClampPosition(w));
                break;
            case ScintillaMessage.GotoPos:
                SetEmptySelection(ClampPosition(w));
                EnsureCaretVisible();
                break;
            case ScintillaMessage.SelectAll:
                SetSelection(0, doc.Length);
                break;
            case ScintillaMessage.ScrollCaret:
                EnsureCaretVisible();
                break;
            case ScintillaMessage.SetCaretFore:
                vs.CaretFore = FromRgb(w);
                Redraw();
                break;
            case ScintillaMessage.SetCaretPeriod:
                vs.CaretPeriodMs = (int)w;
                break;

            // Keyboard commands
            case ScintillaMessage.DocumentStart:
            case ScintillaMessage.DocumentStartExtend:
            case ScintillaMessage.DocumentEnd:
            case ScintillaMessage.DocumentEndExtend:
            case ScintillaMessage.VCHome:
            case ScintillaMessage.VCHomeExtend:
            case ScintillaMessage.LineEnd:
            case ScintillaMessage.LineEndExtend:
            case ScintillaMessage.WordLeft:
            case ScintillaMessage.WordLeftExtend:
            case ScintillaMessage.WordRight:
            case ScintillaMessage.WordRightExtend:
            case ScintillaMessage.DelWordLeft:
            case ScintillaMessage.DelWordRight:
                Execute((EditorCommand)message);
                break;

            // Styles
            case ScintillaMessage.StyleClearAll:
                vs.ClearAll();
                RefreshStyle();
                break;
            case ScintillaMessage.StyleSetFore:
            case ScintillaMessage.StyleSetBack:
            case ScintillaMessage.StyleSetItalic:
            case ScintillaMessage.StyleSetSize:
            case ScintillaMessage.StyleSetSizeFractional:
            case ScintillaMessage.StyleSetEOLFilled:
            case ScintillaMessage.StyleSetFont:
                if (w is >= 0 and < ViewStyle.MaxStyles) SetStyle(vs.Styles[w], message, l);
                break;
            case ScintillaMessage.StyleGetFore:
                return w is >= 0 and < ViewStyle.MaxStyles ? ToRgb(vs.Styles[w].Fore) : 0;
            case ScintillaMessage.StyleGetBack:
                return w is >= 0 and < ViewStyle.MaxStyles ? ToRgb(vs.Styles[w].Back) : 0;
            case ScintillaMessage.StyleGetItalic:
                return w is >= 0 and < ViewStyle.MaxStyles && vs.Styles[w].Italic ? 1 : 0;
            case ScintillaMessage.SetElementColour:
                vs.Elements[(Element)w] = FromRgba(l);
                Redraw();
                break;
            case ScintillaMessage.ResetElementColour:
                vs.Elements.Remove((Element)w);
                Redraw();
                break;
            case ScintillaMessage.SetSelBack:
                if (w != 0) vs.Elements[Element.SelectionBack] = FromRgb(l);
                else vs.Elements.Remove(Element.SelectionBack);
                Redraw();
                break;

            // Styling
            case ScintillaMessage.GetStyleAt:
                return w >= 0 && w < doc.Length ? doc.StyleAt((int)w) : 0;
            case ScintillaMessage.StartStyling:
                doc.StartStyling(ClampPosition(w));
                break;
            case ScintillaMessage.SetStyling:
                doc.SetStyleFor((int)Math.Max(0, w), (byte)l);
                break;

            // Margins
            case ScintillaMessage.SetMarginTypeN:
                if (w is >= 0 and < ViewStyle.MaxMargins) vs.Margins[w].Type = (MarginType)l;
                Redraw();
                break;
            case ScintillaMessage.SetMarginWidthN:
                if (w is >= 0 and < ViewStyle.MaxMargins && l >= 0) SetLayoutMetric(() => vs.Margins[w].Width = (int)l);
                break;
            case ScintillaMessage.GetMarginWidthN:
                return w is >= 0 and < ViewStyle.MaxMargins ? vs.Margins[w].Width : 0;
            case ScintillaMessage.GetMargins:
                return ViewStyle.MaxMargins;
            case ScintillaMessage.SetMarginLeft:
                SetLayoutMetric(() => vs.LeftMarginWidth = (int)Math.Max(0, l));
                break;
            case ScintillaMessage.SetMarginRight:
                SetLayoutMetric(() => vs.RightMarginWidth = (int)Math.Max(0, l));
                break;
            case ScintillaMessage.GetMarginLeft:
                return vs.LeftMarginWidth;
            case ScintillaMessage.GetMarginRight:
                return vs.RightMarginWidth;
            case ScintillaMessage.MarginSetText:
                if (l != 0) return Pointer(message, l);
                MarginSetText((int)w, null);
                break;
            case ScintillaMessage.MarginSetStyle:
                if (w >= 0 && w < doc.LinesTotal) doc.MarginSetStyle((int)w, (int)l);
                break;
            case ScintillaMessage.MarginTextClearAll:
                doc.MarginClearAll();
                Redraw();
                break;
            case ScintillaMessage.TextWidth:
                return l == 0 ? 0 : Pointer(message, l);
            case ScintillaMessage.TextHeight:
                return (nint)vs.LineHeight;

            // Indicators
            case ScintillaMessage.IndicSetStyle:
                if (w is >= 0 and < ViewStyle.MaxIndicators) vs.Indicators[w].Style = (IndicatorStyle)l;
                Redraw();
                break;
            case ScintillaMessage.IndicSetFore:
                if (w is >= 0 and < ViewStyle.MaxIndicators) vs.Indicators[w].Fore = FromRgb(l);
                Redraw();
                break;
            case ScintillaMessage.SetIndicatorCurrent:
                doc.SetIndicatorCurrent((int)w);
                break;
            case ScintillaMessage.IndicatorFillRange:
                doc.DecorationFillRange(ClampPosition(w), doc.Decorations.CurrentValue, (int)Math.Max(0, l));
                break;
            case ScintillaMessage.IndicatorClearRange:
                doc.DecorationFillRange(ClampPosition(w), 0, (int)Math.Max(0, l));
                break;
            case ScintillaMessage.IndicatorValueAt:
                return doc.Decorations.ValueAt((int)w, (int)l);

            // View
            case ScintillaMessage.SetTabWidth:
                if (w > 0) SetLayoutMetric(() => vs.TabWidth = (int)w);
                break;
            case ScintillaMessage.SetUseTabs:
                vs.UseTabs = w != 0;
                break;
            case ScintillaMessage.SetWrapMode:
                if (vs.WrapWord != (w != 0))
                {
                    vs.WrapWord = w != 0;
                    xOffset = 0;
                    NeedWrapping();
                }
                break;
            case ScintillaMessage.WrapCount:
                return w >= 0 && w < doc.LinesTotal ? cs.GetHeight((int)w) : 1;
            case ScintillaMessage.VisibleFromDocLine:
                return w < 0 ? 0 : w >= doc.LinesTotal ? cs.LinesDisplayed : cs.DisplayFromDoc((int)w);
            case ScintillaMessage.DocLineFromVisible:
                return cs.DocFromDisplay((int)Math.Max(0, w));
            case ScintillaMessage.SetFirstVisibleLine:
                ScrollTo((int)w);
                break;
            case ScintillaMessage.GetFirstVisibleLine:
                return topLine;
            case ScintillaMessage.LinesOnScreen:
                return LinesOnScreen;
            case ScintillaMessage.LineScroll:
                ScrollTo(topLine + (int)l);
                if (!Wrapping && w != 0) SetXOffset(xOffset + w * vs.SpaceWidth);
                break;
            case ScintillaMessage.SetXOffset:
                SetXOffset(w);
                break;
            case ScintillaMessage.GetXOffset:
                return (nint)xOffset;
            case ScintillaMessage.SetScrollWidth:
                if (w > 0 && w != scrollWidth)
                {
                    scrollWidth = (int)w;
                    Redraw();
                }
                break;
            case ScintillaMessage.GetScrollWidth:
                return scrollWidth;
            case ScintillaMessage.SetScrollWidthTracking:
                scrollWidthTracking = w != 0;
                break;
            case ScintillaMessage.SetLayoutCache:
                break;
            case ScintillaMessage.PositionFromPoint:
                return PositionFromLocation(w, l, false);
            case ScintillaMessage.PositionFromPointClose:
                return PositionFromLocation(w, l, true);
            case ScintillaMessage.PointXFromPosition:
                return (nint)LocationFromPosition(ClampPosition(l)).X;
            case ScintillaMessage.PointYFromPosition:
                return (nint)LocationFromPosition(ClampPosition(l)).Y;
            case ScintillaMessage.SetFocus:
                Focus(w != 0);
                break;
        }
        return 0;
    }

    private static nint Pointer(ScintillaMessage message, nint l) =>
        l == 0 ? 0 : throw new NotSupportedException($"{message} carries a pointer; use the typed Editor method.");

    private int ClampPosition(nint position) => (int)Math.Clamp(position, 0, doc.Length);

    private static SKColor FromRgb(nint value) => new((byte)value, (byte)(value >> 8), (byte)(value >> 16));

    private static SKColor FromRgba(nint value) =>
        new((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));

    private static nint ToRgb(SKColor colour) => colour.Red | (colour.Green << 8) | (colour.Blue << 16);

    private void SetStyle(Style style, ScintillaMessage message, nint l)
    {
        switch (message)
        {
            case ScintillaMessage.StyleSetFore:
                style.Fore = FromRgb(l);
                break;
            case ScintillaMessage.StyleSetBack:
                style.Back = FromRgb(l);
                break;
            case ScintillaMessage.StyleSetItalic:
                style.Italic = l != 0;
                break;
            case ScintillaMessage.StyleSetSize:
                style.Size = l;
                break;
            case ScintillaMessage.StyleSetSizeFractional:
                style.Size = l / 100f;
                break;
            case ScintillaMessage.StyleSetEOLFilled:
                style.EolFilled = l != 0;
                break;
            case ScintillaMessage.StyleSetFont:
                break; // The editor draws with its one typeface; the name is not read.
        }
        RefreshStyle();
    }

    // Typed messages whose lParam is a pointer in Scintilla.

    public void AddText(ReadOnlySpan<byte> utf8)
    {
        var inserted = doc.InsertString(Caret, utf8);
        SetEmptySelection(Caret + inserted);
        EnsureCaretVisible();
    }

    /// <summary>Adds text at the caret without recording it for undo, for loading a document.</summary>
    public void LoadText(ReadOnlySpan<byte> utf8)
    {
        doc.SetUndoCollection(false);
        try
        {
            AddText(utf8);
        }
        finally
        {
            doc.SetUndoCollection(true);
        }
    }

    public void ReplaceSelection(ReadOnlySpan<byte> utf8)
    {
        if (doc.ReadOnly) return;
        doc.BeginUndoAction();
        ClearSelection();
        var inserted = doc.InsertString(Caret, utf8);
        doc.EndUndoAction();
        SetEmptySelection(Caret + inserted);
        EnsureCaretVisible();
    }

    public byte[] GetText() => doc.GetBytes(0, doc.Length);

    public byte[] GetSelectedText() => doc.GetBytes(sel.Start.Position, sel.Length);

    public byte[] GetTextRange(int start, int end)
    {
        if (end < 0) end = doc.Length;
        start = Math.Clamp(start, 0, doc.Length);
        end = Math.Clamp(end, start, doc.Length);
        return doc.GetBytes(start, end - start);
    }

    public void SetStylingEx(ReadOnlySpan<byte> styles) => doc.SetStyles(styles);

    public void MarginSetText(int line, string? text)
    {
        if (line >= 0 && line < doc.LinesTotal) doc.MarginSetText(line, text);
    }

    public int TextWidth(int style, string text) =>
        style is >= 0 and < ViewStyle.MaxStyles
            ? (int)MathF.Round(shaper.Measure(vs.Styles[style].FontSpec, Encoding.UTF8.GetBytes(text)))
            : 0;

    // Input

    /// <summary>Committed text, inserted one character at a time like InsertCharacter.</summary>
    public void InsertText(string text)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.EncodeToUtf8(buffer);
            InsertCharacter(buffer[..length]);
        }
    }

    private void InsertCharacter(ReadOnlySpan<byte> utf8)
    {
        if (doc.ReadOnly) return;
        var group = !sel.Empty || overtype;
        if (group) doc.BeginUndoAction();
        if (!sel.Empty)
            ClearSelection();
        else if (overtype && Caret < doc.LineEndPosition(Caret))
            doc.DeleteChars(Caret, doc.NextPosition(Caret, 1) - Caret);
        var inserted = doc.InsertString(Caret, utf8);
        if (group) doc.EndUndoAction();
        SetEmptySelection(Caret + inserted);
        EnsureCaretVisible();
        xChosen = -1;
    }

    public bool Key(EditorKey key, EditorModifiers modifiers)
    {
        if (!KeyMap.TryFind(key, modifiers, out var command)) return false;
        Execute(command);
        return true;
    }

    private void Execute(EditorCommand command)
    {
        switch (command)
        {
            case EditorCommand.LineDown:
            case EditorCommand.LineDownExtend:
                MoveVertically(1, command == EditorCommand.LineDownExtend);
                return;
            case EditorCommand.LineUp:
            case EditorCommand.LineUpExtend:
                MoveVertically(-1, command == EditorCommand.LineUpExtend);
                return;
            case EditorCommand.PageDown:
            case EditorCommand.PageDownExtend:
                Page(1, command == EditorCommand.PageDownExtend);
                return;
            case EditorCommand.PageUp:
            case EditorCommand.PageUpExtend:
                Page(-1, command == EditorCommand.PageUpExtend);
                return;
            case EditorCommand.LineScrollDown:
                ScrollTo(topLine + 1);
                return;
            case EditorCommand.LineScrollUp:
                ScrollTo(topLine - 1);
                return;
            case EditorCommand.CharLeft:
                MoveTo(sel.Empty ? doc.NextPosition(Caret, -1) : sel.Start.Position, false);
                return;
            case EditorCommand.CharLeftExtend:
                MoveTo(doc.NextPosition(Caret, -1), true);
                return;
            case EditorCommand.CharRight:
                MoveTo(sel.Empty ? doc.NextPosition(Caret, 1) : sel.End.Position, false);
                return;
            case EditorCommand.CharRightExtend:
                MoveTo(doc.NextPosition(Caret, 1), true);
                return;
            case EditorCommand.WordLeft:
            case EditorCommand.WordLeftExtend:
                MoveTo(doc.NextWordStart(Caret, -1), command == EditorCommand.WordLeftExtend);
                return;
            case EditorCommand.WordRight:
            case EditorCommand.WordRightExtend:
                MoveTo(doc.NextWordStart(Caret, 1), command == EditorCommand.WordRightExtend);
                return;
            case EditorCommand.VCHome:
            case EditorCommand.VCHomeExtend:
                MoveTo(doc.VCHomePosition(Caret), command == EditorCommand.VCHomeExtend);
                return;
            case EditorCommand.LineEnd:
            case EditorCommand.LineEndExtend:
                MoveTo(doc.LineEndPosition(Caret), command == EditorCommand.LineEndExtend);
                return;
            case EditorCommand.HomeDisplay:
            case EditorCommand.LineEndDisplay:
                {
                    var (line, subLine) = DisplayRowOf(Caret);
                    var x = command == EditorCommand.HomeDisplay ? float.MinValue / 2 : float.MaxValue / 2;
                    MoveTo(view.PositionInLine(doc, vs, line, subLine, x, false, WrapWidth), false);
                    return;
                }
            case EditorCommand.DocumentStart:
            case EditorCommand.DocumentStartExtend:
                MoveTo(0, command == EditorCommand.DocumentStartExtend);
                return;
            case EditorCommand.DocumentEnd:
            case EditorCommand.DocumentEndExtend:
                MoveTo(doc.Length, command == EditorCommand.DocumentEndExtend);
                return;
            case EditorCommand.DelWordLeft:
                DeleteRangeAndCollapse(doc.NextWordStart(Caret, -1), Caret);
                return;
            case EditorCommand.DelWordRight:
                DeleteRangeAndCollapse(Caret, doc.NextWordStart(Caret, 1));
                return;
            case EditorCommand.DelLineLeft:
                DeleteRangeAndCollapse(doc.LineStartPosition(Caret), Caret);
                return;
            case EditorCommand.DelLineRight:
                DeleteRangeAndCollapse(Caret, doc.LineEndPosition(Caret));
                return;
            case EditorCommand.Clear:
                Clear();
                return;
            case EditorCommand.DeleteBack:
                DeleteBack();
                return;
            case EditorCommand.Undo:
                Undo();
                return;
            case EditorCommand.EditToggleOvertype:
                overtype = !overtype;
                Redraw();
                return;
            case EditorCommand.Cancel:
                return;
            case EditorCommand.NewLine:
                ReplaceSelection(doc.EolBytes);
                return;
            case EditorCommand.Tab:
                Indent(true);
                return;
            case EditorCommand.BackTab:
                Indent(false);
                return;
        }
    }

    private void MoveTo(int position, bool extend)
    {
        if (extend) SetSelection(position, Anchor);
        else SetEmptySelection(position);
        EnsureCaretVisible();
        xChosen = -1;
    }

    private (int Line, int SubLine) DisplayRowOf(int position)
    {
        var line = doc.LineFromPosition(position);
        var location = view.LocationInLine(doc, vs, position, WrapWidth);
        var subLine = vs.LineHeight > 0 ? (int)(location.Y / vs.LineHeight) : 0;
        return (line, Math.Clamp(subLine, 0, cs.GetHeight(line) - 1));
    }

    private int DisplayLineOf(int position)
    {
        var (line, subLine) = DisplayRowOf(position);
        return cs.DisplayFromDoc(line) + subLine;
    }

    private int PositionOnDisplayLine(int displayLine, float x)
    {
        displayLine = Math.Clamp(displayLine, 0, Math.Max(0, cs.LinesDisplayed - 1));
        var line = cs.DocFromDisplay(displayLine);
        return view.PositionInLine(doc, vs, line, displayLine - cs.DisplayFromDoc(line), x, false, WrapWidth);
    }

    private void MoveVertically(int direction, bool extend)
    {
        var x = xChosen >= 0 ? xChosen : view.LocationInLine(doc, vs, Caret, WrapWidth).X;
        var current = DisplayLineOf(Caret);
        var target = current + direction;
        var position = target < 0 ? Caret : target >= cs.LinesDisplayed ? Caret : PositionOnDisplayLine(target, x);
        if (extend) SetSelection(position, Anchor);
        else SetEmptySelection(position);
        EnsureCaretVisible();
        xChosen = x;
    }

    private void Page(int direction, bool extend)
    {
        var x = xChosen >= 0 ? xChosen : view.LocationInLine(doc, vs, Caret, WrapWidth).X;
        var page = Math.Max(1, LinesOnScreen - 1);
        var target = DisplayLineOf(Caret) + direction * page;
        var position = target < 0 ? 0 : target >= cs.LinesDisplayed ? doc.Length : PositionOnDisplayLine(target, x);
        ScrollTo(topLine + direction * page);
        if (extend) SetSelection(position, Anchor);
        else SetEmptySelection(position);
        EnsureCaretVisible();
        xChosen = x;
    }

    private void DeleteRangeAndCollapse(int start, int end)
    {
        if (end > start) doc.DeleteChars(start, end - start);
        SetEmptySelection(Math.Min(start, doc.Length));
        EnsureCaretVisible();
    }

    private void ClearSelection()
    {
        if (sel.Empty) return;
        var start = sel.Start.Position;
        if (doc.DeleteChars(start, sel.Length)) SetEmptySelection(start);
    }

    private void Clear()
    {
        if (!sel.Empty)
            ClearSelection();
        else if (Caret < doc.Length)
            doc.DeleteChars(Caret, doc.NextPosition(Caret, 1) - Caret);
        EnsureCaretVisible();
    }

    private void DeleteBack()
    {
        if (!sel.Empty)
            ClearSelection();
        else if (Caret > 0)
        {
            var previous = doc.NextPosition(Caret, -1);
            doc.DeleteChars(previous, Caret - previous);
        }
        EnsureCaretVisible();
        xChosen = -1;
    }

    private void ClearAll()
    {
        doc.BeginUndoAction();
        if (doc.Length > 0) doc.DeleteChars(0, doc.Length);
        doc.EndUndoAction();
        if (!doc.ReadOnly)
        {
            cs.Clear();
            doc.MarginClearAll();
        }
        SetEmptySelection(0);
        topLine = 0;
        xOffset = 0;
        Redraw();
    }

    private void Undo()
    {
        if (!doc.CanUndo) return;
        var position = doc.Undo();
        if (position >= 0) SetEmptySelection(position);
        EnsureCaretVisible();
    }

    private void Redo()
    {
        if (!doc.CanRedo) return;
        var position = doc.Redo();
        if (position >= 0) SetEmptySelection(position);
        EnsureCaretVisible();
    }

    private ReadOnlySpan<byte> IndentText(int column) =>
        vs.UseTabs ? "\t"u8 : Encoding.ASCII.GetBytes(new string(' ', vs.TabWidth - column % vs.TabWidth));

    /// <summary>Tab and BackTab: within a line inserts (or removes) indentation; across lines indents each line.</summary>
    private void Indent(bool forward)
    {
        if (doc.ReadOnly) return;
        var startLine = doc.LineFromPosition(sel.Start.Position);
        var endLine = doc.LineFromPosition(sel.End.Position);
        if (startLine == endLine)
        {
            if (forward)
            {
                ReplaceSelection(IndentText(Column(sel.Start.Position)));
                return;
            }
            // BackTab on one line dedents it (upstream: when the caret is within the indentation).
            endLine = startLine;
        }
        else if (sel.End.Position == doc.LineStart(endLine))
            endLine--;

        var caret = Caret;
        var anchor = Anchor;
        doc.BeginUndoAction();
        for (var line = startLine; line <= endLine; line++)
        {
            var lineStart = doc.LineStart(line);
            int delta;
            if (forward)
            {
                if (doc.LineEnd(line) == lineStart) continue;
                delta = doc.InsertString(lineStart, IndentText(0));
            }
            else
            {
                var remove = 0;
                if (doc.CharAt(lineStart) == '\t')
                    remove = 1;
                else
                    while (remove < vs.TabWidth && lineStart + remove < doc.LineEnd(line) && doc.CharAt(lineStart + remove) == ' ')
                        remove++;
                if (remove == 0) continue;
                doc.DeleteChars(lineStart, remove);
                delta = -remove;
            }
            if (caret > lineStart || (caret == lineStart && !forward)) caret = Math.Max(lineStart, caret + delta);
            if (anchor > lineStart || (anchor == lineStart && !forward)) anchor = Math.Max(lineStart, anchor + delta);
        }
        doc.EndUndoAction();
        SetSelection(caret, anchor);
        EnsureCaretVisible();
    }

    private int Column(int position)
    {
        var column = 0;
        for (var i = doc.LineStartPosition(position); i < position; i = doc.NextPosition(i, 1))
            column = doc.CharAt(i) == '\t' ? (column / vs.TabWidth + 1) * vs.TabWidth : column + 1;
        return column;
    }

    // Mouse

    public void Mouse(MouseAction action, float x, float y, uint time, EditorModifiers modifiers)
    {
        switch (action)
        {
            case MouseAction.Down:
                ButtonDown(new SKPoint(x, y), time, (modifiers & EditorModifiers.Shift) != 0);
                break;
            case MouseAction.Move:
                if (captured) ButtonMove(new SKPoint(x, y));
                break;
            case MouseAction.Up:
                if (captured && !(x < 0 && y < 0)) ButtonMove(new SKPoint(x, y));
                captured = false;
                break;
        }
    }

    private bool InMargin(SKPoint point) => point.X >= client.Left && point.X < client.Left + vs.FixedColumnWidth;

    private void ButtonDown(SKPoint point, uint time, bool shift)
    {
        var doubleClick = time - lastClickTime < DoubleClickTime
            && Math.Abs(point.X - lastClick.X) <= DoubleClickClose && Math.Abs(point.Y - lastClick.Y) <= DoubleClickClose;
        var position = PositionFromLocation(point.X, point.Y, false);
        var inMargin = InMargin(point);

        if (inMargin)
            unit = SelectionUnit.Line;
        else if (doubleClick)
            unit = unit switch { SelectionUnit.Character => SelectionUnit.Word, SelectionUnit.Word => SelectionUnit.Line, _ => SelectionUnit.Character };
        else
            unit = SelectionUnit.Character;

        if (shift && !doubleClick)
        {
            originalStart = originalEnd = Anchor;
            if (unit == SelectionUnit.Line)
            {
                var anchorLine = doc.LineFromPosition(Anchor);
                originalStart = doc.LineStart(anchorLine);
                originalEnd = doc.LineStart(anchorLine + 1);
            }
            ExtendTo(position);
        }
        else
        {
            switch (unit)
            {
                case SelectionUnit.Word:
                    originalStart = doc.ExtendWordSelect(position, -1);
                    originalEnd = doc.ExtendWordSelect(position, 1);
                    SetSelection(originalEnd, originalStart);
                    break;
                case SelectionUnit.Line:
                    var line = doc.LineFromPosition(position);
                    originalStart = doc.LineStart(line);
                    originalEnd = doc.LineStart(line + 1);
                    SetSelection(originalEnd, originalStart);
                    break;
                default:
                    originalStart = originalEnd = position;
                    SetEmptySelection(position);
                    break;
            }
        }
        lastClickTime = time;
        lastClick = point;
        lastPointer = point;
        captured = true;
        xChosen = -1;
        EnsureCaretVisible();
    }

    private void ButtonMove(SKPoint point)
    {
        lastPointer = point;
        ExtendTo(PositionFromLocation(point.X, point.Y, false));
    }

    private void ExtendTo(int position)
    {
        switch (unit)
        {
            case SelectionUnit.Word:
                if (position > originalEnd) SetSelection(doc.ExtendWordSelect(position, 1), originalStart);
                else if (position < originalStart) SetSelection(doc.ExtendWordSelect(position, -1), originalEnd);
                else SetSelection(originalEnd, originalStart);
                break;
            case SelectionUnit.Line:
                var line = doc.LineFromPosition(position);
                if (position >= originalEnd) SetSelection(doc.LineStart(line + 1), originalStart);
                else if (position < originalStart) SetSelection(doc.LineStart(line), originalEnd);
                else SetSelection(originalEnd, originalStart);
                break;
            default:
                SetSelection(position, originalStart);
                break;
        }
        EnsureCaretVisible();
    }

    public void Focus(bool focused)
    {
        this.focused = focused;
        caretOn = focused;
        if (!focused) captured = false;
        Redraw();
    }

    /// <summary>Caret blink, and drag auto-scroll while the mouse is captured.</summary>
    public void Tick()
    {
        if (focused && vs.CaretPeriodMs > 0)
        {
            caretOn = !caretOn;
            Redraw();
        }
        if (!captured) return;
        var text = TextRect;
        var lines = lastPointer.Y < text.Top ? -1 : lastPointer.Y >= text.Bottom ? 1 : 0;
        if (lines != 0) ScrollTo(topLine + lines);
        if (!Wrapping)
        {
            if (lastPointer.X < text.Left) SetXOffset(xOffset - vs.SpaceWidth * 4);
            else if (lastPointer.X >= text.Right) SetXOffset(xOffset + vs.SpaceWidth * 4);
        }
        ExtendTo(PositionFromLocation(lastPointer.X, lastPointer.Y, false));
    }

    // Selection

    private void SetSelection(int caret, int anchor)
    {
        caret = Math.Clamp(caret, 0, doc.Length);
        anchor = Math.Clamp(anchor, 0, doc.Length);
        if (caret == Caret && anchor == Anchor) return;
        sel.SetMain(caret, anchor);
        caretOn = true;
        Redraw();
    }

    private void SetEmptySelection(int position) => SetSelection(position, position);

    // Scrolling and coordinates

    private void ScrollTo(int line)
    {
        line = Math.Clamp(line, 0, MaxScrollPos);
        if (line == topLine) return;
        topLine = line;
        WrapVisible();
        Redraw();
    }

    private void SetXOffset(float offset)
    {
        offset = Math.Max(0, offset);
        if (offset == xOffset) return;
        xOffset = offset;
        Redraw();
    }

    /// <summary>Scrolls minimally (upstream default caret policy: slop for x, even for y) to show the caret.</summary>
    private void EnsureCaretVisible()
    {
        var location = view.LocationInLine(doc, vs, Caret, WrapWidth);
        var line = doc.LineFromPosition(Caret);
        var subLine = vs.LineHeight > 0 ? (int)(location.Y / vs.LineHeight) : 0;
        var displayLine = cs.DisplayFromDoc(line) + Math.Clamp(subLine, 0, cs.GetHeight(line) - 1);
        if (displayLine < topLine) ScrollTo(displayLine);
        else if (displayLine >= topLine + LinesOnScreen) ScrollTo(displayLine - LinesOnScreen + 1);

        if (Wrapping)
        {
            SetXOffset(0);
            return;
        }
        var width = TextRect.Width;
        if (location.X < xOffset) SetXOffset(location.X - CaretSlop);
        else if (location.X > xOffset + width - 1) SetXOffset(location.X - width + CaretSlop);
    }

    private SKPoint LocationFromPosition(int position)
    {
        var line = doc.LineFromPosition(position);
        var location = view.LocationInLine(doc, vs, position, WrapWidth);
        var y = (cs.DisplayFromDoc(line) - topLine) * vs.LineHeight + location.Y + client.Top;
        var x = location.X - xOffset + client.Left + vs.FixedColumnWidth;
        return new SKPoint(x, y);
    }

    /// <summary>Document position at a client point; with <paramref name="canReturnInvalid"/>, -1 outside the text.</summary>
    private int PositionFromLocation(float x, float y, bool canReturnInvalid)
    {
        if (canReturnInvalid && (!client.Contains(x, y) || InMargin(new SKPoint(x, y)))) return -1;
        var height = vs.LineHeight > 0 ? vs.LineHeight : 1;
        var displayLine = topLine + (int)MathF.Floor((y - client.Top) / height);
        if (displayLine < 0) return canReturnInvalid ? -1 : 0;
        if (displayLine >= cs.LinesDisplayed) return canReturnInvalid ? -1 : doc.Length;
        var line = cs.DocFromDisplay(displayLine);
        var subLine = displayLine - cs.DisplayFromDoc(line);
        var textX = x - client.Left - vs.FixedColumnWidth + xOffset;
        var position = view.PositionInLine(doc, vs, line, subLine, textX, false, WrapWidth);
        if (canReturnInvalid)
        {
            // Close only within the drawn row: reject points past the row's last character.
            var lineEnd = doc.LineEnd(line);
            if (position >= lineEnd && textX > view.LocationInLine(doc, vs, lineEnd, WrapWidth).X) return -1;
            if (textX < 0) return -1;
        }
        return position;
    }

    // Wrapping

    /// <summary>Marks every line for re-wrapping, wraps the visible ones now and leaves the rest to <see cref="Idle"/>.</summary>
    private void NeedWrapping()
    {
        view.Invalidate();
        wrapPending = 0;
        if (!Wrapping)
        {
            var docTop = cs.DocFromDisplay(topLine);
            cs.Clear();
            cs.InsertLines(1, doc.LinesTotal - 1);
            wrapPending = int.MaxValue;
            topLine = Math.Clamp(docTop, 0, MaxScrollPos);
        }
        else
            WrapVisible();
        Redraw();
    }

    private void WrapLines(int first, int last)
    {
        var width = WrapWidth;
        for (var line = Math.Max(0, first); line <= last && line < doc.LinesTotal; line++)
            cs.SetHeight(line, Math.Max(1, view.WrapLine(doc, vs, line, width)));
    }

    /// <summary>Re-wraps changed lines on screen now and leaves the rest to <see cref="Idle"/>, as upstream does.</summary>
    private void RewrapChanged(int first, int last)
    {
        var docTop = cs.DocFromDisplay(topLine);
        if (first >= docTop && last <= docTop + LinesOnScreen + 1)
        {
            WrapLines(first, last);
            return;
        }
        wrapPending = Math.Min(wrapPending, Math.Max(0, first));
    }

    /// <summary>Wraps the lines on screen while keeping the top document line and its wrapped row in place.</summary>
    private void WrapVisible()
    {
        if (!Wrapping || wrapPending == int.MaxValue) return;
        var docTop = cs.DocFromDisplay(topLine);
        var subTop = topLine - cs.DisplayFromDoc(docTop);
        WrapLines(docTop, docTop + LinesOnScreen + 1);
        topLine = Math.Clamp(cs.DisplayFromDocSub(docTop, subTop), 0, MaxScrollPos);
    }

    /// <summary>One ~10 ms slice of background wrapping; returns whether more remains.</summary>
    public bool Idle()
    {
        if (!Wrapping || wrapPending >= doc.LinesTotal)
        {
            wrapPending = int.MaxValue;
            return false;
        }
        var docTop = cs.DocFromDisplay(topLine);
        var subTop = topLine - cs.DisplayFromDoc(docTop);
        var clock = Stopwatch.StartNew();
        var width = WrapWidth;
        while (wrapPending < doc.LinesTotal && clock.ElapsedMilliseconds < 10)
        {
            cs.SetHeight(wrapPending, Math.Max(1, view.WrapLine(doc, vs, wrapPending, width, cache: false)));
            wrapPending++;
        }
        topLine = Math.Clamp(cs.DisplayFromDocSub(docTop, subTop), 0, MaxScrollPos);
        Redraw();
        if (wrapPending < doc.LinesTotal) return true;
        wrapPending = int.MaxValue;
        return false;
    }

    // View state

    public void SetDirection(ScintillaTextDirection direction)
    {
        shaper.Direction = direction;
        view.Invalidate();
        if (Wrapping) NeedWrapping();
        Redraw();
    }

    public void Resize(float width, float height)
    {
        var widthChanged = width != client.Width;
        client = new SKRect(0, 0, Math.Max(0, width), Math.Max(0, height));
        if (widthChanged && Wrapping) NeedWrapping();
        else WrapVisible();
        topLine = Math.Clamp(topLine, 0, MaxScrollPos);
        Redraw();
    }

    private void SetLayoutMetric(Action change)
    {
        change();
        RefreshStyle();
    }

    /// <summary>Recomputes line metrics from the styles' fonts (upstream ViewStyle::Refresh) and re-lays out.</summary>
    private void RefreshStyle()
    {
        float ascent = 1, descent = 1;
        var seen = new HashSet<FontSpec>();
        foreach (var style in vs.Styles)
        {
            if (!seen.Add(style.FontSpec)) continue;
            var metrics = shaper.Metrics(style.FontSpec);
            // Upstream FontMeasurements floors each metric (ViewStyle.cxx).
            ascent = Math.Max(ascent, MathF.Floor(metrics.Ascent));
            descent = Math.Max(descent, MathF.Floor(metrics.Descent));
        }
        vs.Ascent = ascent;
        vs.Descent = descent;
        vs.LineHeight = MathF.Round(ascent + descent, MidpointRounding.AwayFromZero);
        vs.SpaceWidth = shaper.Measure(vs.Styles[ViewStyle.Default].FontSpec, " "u8);
        var widest = 0f;
        Span<byte> glyph = stackalloc byte[1];
        foreach (var spec in seen)
            for (glyph[0] = 0x20; glyph[0] < 0x7F; glyph[0]++)
                widest = Math.Max(widest, shaper.Measure(spec, glyph));
        vs.MaxAsciiAdvance = widest;
        vs.TabPixels = vs.TabWidth * vs.SpaceWidth;
        view.Invalidate();
        if (Wrapping) NeedWrapping();
        Redraw();
    }

    public void Paint(SKCanvas canvas)
    {
        WrapVisible();
        view.Paint(canvas, new PaintState(doc, vs, cs, sel, client, topLine, xOffset, WrapWidth, focused,
            focused && caretOn, doc.ReadOnly));
        if (!scrollWidthTracking) return;
        var last = cs.DocFromDisplay(topLine + LinesOnScreen);
        for (var line = cs.DocFromDisplay(topLine); line <= last && line < doc.LinesTotal; line++)
        {
            var width = (int)MathF.Ceiling(view.LineWidth(doc, vs, line) + vs.SpaceWidth);
            if (width > scrollWidth) scrollWidth = width;
        }
    }

    // IDocumentWatcher

    void IDocumentWatcher.Modified(DocumentChange change)
    {
        var flags = change.Flags;
        if ((flags & (ModificationFlags.InsertText | ModificationFlags.DeleteText)) != 0)
        {
            var insertion = (flags & ModificationFlags.InsertText) != 0;
            sel.MovePositions(insertion, change.Position, change.Length);
            var lineOfChange = doc.LineFromPosition(change.Position);
            if (change.LinesAdded != 0)
            {
                var lineOfPos = lineOfChange;
                if (change.Position > doc.LineStart(lineOfPos)) lineOfPos++;
                var docTop = cs.DocFromDisplay(topLine);
                if (change.LinesAdded > 0) cs.InsertLines(lineOfPos, change.LinesAdded);
                else cs.DeleteLines(lineOfPos, -change.LinesAdded);
                if (wrapPending != int.MaxValue && wrapPending > lineOfPos)
                    wrapPending = Math.Max(lineOfPos, wrapPending + change.LinesAdded);
                // Avoid scrolling the display for a change above it.
                if (lineOfPos <= docTop && change.Position < doc.LineStart(docTop))
                    topLine = Math.Clamp(cs.DisplayFromDoc(Math.Max(0, docTop + change.LinesAdded)), 0, MaxScrollPos);
            }
            view.Invalidate(lineOfChange);
            if (Wrapping) RewrapChanged(lineOfChange, lineOfChange + Math.Max(0, change.LinesAdded));
            topLine = Math.Clamp(topLine, 0, MaxScrollPos);
            revision++;
        }
        else if ((flags & ModificationFlags.ChangeStyle) != 0)
        {
            var line = doc.LineFromPosition(change.Position);
            view.Invalidate(line);
            if (Wrapping) RewrapChanged(line, doc.LineFromPosition(change.Position + change.Length));
        }
        Redraw();
    }

    void IDocumentWatcher.SavePointChanged(bool atSavePoint) => Redraw();

    public void Dispose()
    {
        doc.RemoveWatcher(this);
        shaper.Dispose();
    }
}