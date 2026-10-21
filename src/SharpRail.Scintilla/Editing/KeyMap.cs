namespace SharpRail.Scintilla.Editing;

/// <summary>Keyboard commands the default key map binds (upstream SCI_* message numbers).</summary>
internal enum EditorCommand
{
    Undo = 2176, Clear = 2180,
    LineDown = 2300, LineDownExtend = 2301, LineUp = 2302, LineUpExtend = 2303,
    CharLeft = 2304, CharLeftExtend = 2305, CharRight = 2306, CharRightExtend = 2307,
    WordLeft = 2308, WordLeftExtend = 2309, WordRight = 2310, WordRightExtend = 2311,
    LineEnd = 2314, LineEndExtend = 2315,
    DocumentStart = 2316, DocumentStartExtend = 2317, DocumentEnd = 2318, DocumentEndExtend = 2319,
    PageUp = 2320, PageUpExtend = 2321, PageDown = 2322, PageDownExtend = 2323,
    EditToggleOvertype = 2324, Cancel = 2325, DeleteBack = 2326, Tab = 2327, BackTab = 2328, NewLine = 2329,
    VCHome = 2331, VCHomeExtend = 2332, DelWordLeft = 2335, DelWordRight = 2336,
    LineScrollDown = 2342, LineScrollUp = 2343, HomeDisplay = 2345, LineEndDisplay = 2347,
    DelLineLeft = 2395, DelLineRight = 2396,
}

/// <summary>Default key bindings from upstream src/KeyMap.cxx, limited to the keys in <see cref="EditorKey"/>.</summary>
/// <remarks>Rectangular-selection (Alt+Shift) and clipboard bindings (Shift+Delete, Shift+Insert, Ctrl+Insert) are
/// left out: SharpRail has no rectangular selection and handles the clipboard in the control.</remarks>
internal static class KeyMap
{
    private const EditorModifiers None = EditorModifiers.None, Shift = EditorModifiers.Shift,
        Ctrl = EditorModifiers.Ctrl, Alt = EditorModifiers.Alt;

    private static readonly Dictionary<(EditorKey, EditorModifiers), EditorCommand> Bindings = new()
    {
        [(EditorKey.Down, None)] = EditorCommand.LineDown,
        [(EditorKey.Down, Shift)] = EditorCommand.LineDownExtend,
        [(EditorKey.Down, Ctrl)] = EditorCommand.LineScrollDown,
        [(EditorKey.Up, None)] = EditorCommand.LineUp,
        [(EditorKey.Up, Shift)] = EditorCommand.LineUpExtend,
        [(EditorKey.Up, Ctrl)] = EditorCommand.LineScrollUp,
        [(EditorKey.Left, None)] = EditorCommand.CharLeft,
        [(EditorKey.Left, Shift)] = EditorCommand.CharLeftExtend,
        [(EditorKey.Left, Ctrl)] = EditorCommand.WordLeft,
        [(EditorKey.Left, Ctrl | Shift)] = EditorCommand.WordLeftExtend,
        [(EditorKey.Right, None)] = EditorCommand.CharRight,
        [(EditorKey.Right, Shift)] = EditorCommand.CharRightExtend,
        [(EditorKey.Right, Ctrl)] = EditorCommand.WordRight,
        [(EditorKey.Right, Ctrl | Shift)] = EditorCommand.WordRightExtend,
        [(EditorKey.Home, None)] = EditorCommand.VCHome,
        [(EditorKey.Home, Shift)] = EditorCommand.VCHomeExtend,
        [(EditorKey.Home, Ctrl)] = EditorCommand.DocumentStart,
        [(EditorKey.Home, Ctrl | Shift)] = EditorCommand.DocumentStartExtend,
        [(EditorKey.Home, Alt)] = EditorCommand.HomeDisplay,
        [(EditorKey.End, None)] = EditorCommand.LineEnd,
        [(EditorKey.End, Shift)] = EditorCommand.LineEndExtend,
        [(EditorKey.End, Ctrl)] = EditorCommand.DocumentEnd,
        [(EditorKey.End, Ctrl | Shift)] = EditorCommand.DocumentEndExtend,
        [(EditorKey.End, Alt)] = EditorCommand.LineEndDisplay,
        [(EditorKey.Prior, None)] = EditorCommand.PageUp,
        [(EditorKey.Prior, Shift)] = EditorCommand.PageUpExtend,
        [(EditorKey.Next, None)] = EditorCommand.PageDown,
        [(EditorKey.Next, Shift)] = EditorCommand.PageDownExtend,
        [(EditorKey.Delete, None)] = EditorCommand.Clear,
        [(EditorKey.Delete, Ctrl)] = EditorCommand.DelWordRight,
        [(EditorKey.Delete, Ctrl | Shift)] = EditorCommand.DelLineRight,
        [(EditorKey.Insert, None)] = EditorCommand.EditToggleOvertype,
        [(EditorKey.Escape, None)] = EditorCommand.Cancel,
        [(EditorKey.Back, None)] = EditorCommand.DeleteBack,
        [(EditorKey.Back, Shift)] = EditorCommand.DeleteBack,
        [(EditorKey.Back, Ctrl)] = EditorCommand.DelWordLeft,
        [(EditorKey.Back, Alt)] = EditorCommand.Undo,
        [(EditorKey.Back, Ctrl | Shift)] = EditorCommand.DelLineLeft,
        [(EditorKey.Tab, None)] = EditorCommand.Tab,
        [(EditorKey.Tab, Shift)] = EditorCommand.BackTab,
        [(EditorKey.Return, None)] = EditorCommand.NewLine,
        [(EditorKey.Return, Shift)] = EditorCommand.NewLine,
    };

    /// <summary>The command bound to a key chord, if any.</summary>
    public static bool TryFind(EditorKey key, EditorModifiers modifiers, out EditorCommand command) =>
        Bindings.TryGetValue((key, modifiers), out command);
}