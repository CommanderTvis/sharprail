using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace SharpRail.Plugins.Isolation;

/// <summary>
/// The surface protocol's caps (Security.SPEC.md, UI halves). The host's relay holds a plugin's frames to them before
/// any reaches a client.
/// </summary>
public static class SurfaceLimits
{
    /// <summary>The side of the tiles the runner diffs and sends.</summary>
    public const int TileSide = 64;
    /// <summary>The largest tile side a frame may carry.</summary>
    public const int MostTileSide = 256;
    /// <summary>Tiles in one frame message.</summary>
    public const int MostTiles = 4096;
    /// <summary>A surface's largest side in device pixels.</summary>
    public const int MostSide = 8192;
    /// <summary>Pixels a whole frame may decode to, at 4 bytes each: 64 MiB.</summary>
    public const long MostFramePixels = 16L * 1024 * 1024;
    /// <summary>Tile bytes in one frame message; a larger frame is several messages, the last marked.</summary>
    public const int MessageBytes = 1024 * 1024;
    /// <summary>Surfaces one client may hold open in one plugin.</summary>
    public const int MostSurfaces = 64;
    /// <summary>Popups one surface may hold open at once (a menu and its submenus, a flyout, a tooltip).</summary>
    public const int MostPopups = 8;
    /// <summary>Characters of a dialog's title: plain text with no control characters.</summary>
    public const int MostTitle = 128;
    /// <summary>The largest cursor shape number (Avalonia's <c>StandardCursorType</c> values lie below it).</summary>
    public const int MostCursorShape = 63;
    /// <summary>Accessibility nodes one top-level of a surface (the surface, a popup or a dialog) may describe.</summary>
    public const int MostNodes = 512;
    /// <summary>How deep a top-level's accessibility tree may nest.</summary>
    public const int MostNodeDepth = 64;
    /// <summary>Characters of an accessibility node's name and value, and of its automation id.</summary>
    public const int MostNodeText = 200, MostAutomationId = 64;
    /// <summary>The largest role number (Avalonia's <c>AutomationControlType</c> values lie below it).</summary>
    public const int MostRole = 127;
    /// <summary>Every state and action a node may carry.</summary>
    public const SurfaceNodeStates KnownNodeStates = (SurfaceNodeStates)0x3ff | SurfaceNodeStates.Invoke | SurfaceNodeStates.Value |
        SurfaceNodeStates.ExpandCollapse | SurfaceNodeStates.Toggle;
    public const double LeastScale = 0.25, MostScale = 8;
}

/// <summary>What a forwarded input event is.</summary>
/// <remarks>
/// <see cref="Preedit"/> is the client's input method composing <see cref="SurfaceInput.Text"/> (empty when it stops) with
/// its caret at <see cref="SurfaceInput.Caret"/>; the text it commits arrives as <see cref="Text"/>. <see cref="CloseDialog"/>
/// asks the dialog <see cref="SurfaceInput.Popup"/> to close, as its frame's close button does.
/// </remarks>
/// <remarks>
/// The accessibility actions (<see cref="Invoke"/> to <see cref="Toggle"/>) act on the node <see cref="SurfaceInput.Node"/>
/// of the top-level <see cref="SurfaceInput.Popup"/>; <see cref="SetValue"/> sets it to <see cref="SurfaceInput.Text"/>.
/// </remarks>
public enum SurfaceInputKind
{
    None, PointerMove, PointerDown, PointerUp, PointerLeave, Wheel, KeyDown, KeyUp, Text, Preedit, CloseDialog,
    Invoke, SetFocus, SetValue, Expand, Collapse, Toggle
}

/// <summary>
/// The host asks the runner to call a factory the plugin lent on a client's context and show its control as
/// <see cref="Surface"/>, a number the host chose. <see cref="Theme"/> is the client's kit theme, applied first.
/// </summary>
public sealed record SurfaceOpen(long Context, int Surface, int Member, long Handle, string Payload, string Theme);

/// <summary>A surface's size in device-independent pixels at the client's scale. Each new size starts from a transparent frame.</summary>
public sealed record SurfaceResize(int Surface, double Width, double Height, double Scale);

/// <summary>
/// One input event at a point in device-independent pixels, on the surface or on its open <see cref="Popup"/> (0 is
/// the surface itself). <see cref="Button"/>, <see cref="Key"/>, <see cref="PhysicalKey"/> and <see cref="Modifiers"/>
/// carry Avalonia's own values. <see cref="Caret"/> is a composition's caret in its text. <see cref="Node"/> is the
/// accessibility node an accessibility action acts on.
/// </summary>
public sealed record SurfaceInput(int Surface, SurfaceInputKind Kind, double X = 0, double Y = 0, int Button = 0, int Modifiers = 0,
    double DeltaX = 0, double DeltaY = 0, int Key = 0, int PhysicalKey = 0, string Text = "", int Popup = 0, int Caret = 0, int Node = 0);

/// <summary>The client applied frame <see cref="Sequence"/> of the surface or of its <see cref="Popup"/>; the runner may send the next.</summary>
public sealed record SurfaceAck(int Surface, long Sequence, int Popup = 0);

/// <summary>The client's user pressed outside the surface and its popups: the runner closes the popups that close on such a press.</summary>
public sealed record SurfaceDismiss(int Surface);

/// <summary>
/// A popup of the surface (a menu, a flyout, a tooltip) opened, moved or closed. <see cref="Popup"/> numbers it within
/// the surface from 1, never reused. <see cref="X"/> and <see cref="Y"/> place it from the surface's top-left corner and
/// <see cref="Width"/> and <see cref="Height"/> size it, in device-independent pixels; it may extend outside the
/// surface. A popup that does not take <see cref="Input"/> (a tooltip) lets the pointer through to what lies under it.
/// A <see cref="Dialog"/> is a window the plugin opened: the client places it itself, in a frame that names the plugin
/// and the dialog's <see cref="Title"/>, and its place here means nothing.
/// </summary>
public sealed record SurfacePopup(int Surface, int Popup, double X, double Y, double Width, double Height, bool Open, bool Input = true,
    bool Dialog = false, string Title = "");

/// <summary>The cursor the plugin shows over the surface or its <see cref="Popup"/>: Avalonia's <c>StandardCursorType</c> value.</summary>
public sealed record SurfaceCursor(int Surface, int Popup, int Shape);

/// <summary>
/// Whether a text field that composes with an input method has focus in the surface or its <see cref="Popup"/>, and its
/// caret, in device-independent pixels from that top-level's corner: the client's input method shows its composition
/// there.
/// </summary>
public sealed record SurfaceIme(int Surface, int Popup, bool Active, double X, double Y, double Width, double Height);

/// <summary>What an accessibility node is and does: its states and the actions a client may ask of it.</summary>
[Flags]
public enum SurfaceNodeStates
{
    None = 0, Enabled = 1, Focusable = 2, Focused = 4, Offscreen = 8, Expanded = 16, Collapsed = 32, Checked = 64, Indeterminate = 128,
    ReadOnly = 256, Selected = 512,
    Invoke = 1 << 16, Value = 1 << 17, ExpandCollapse = 1 << 18, Toggle = 1 << 19
}

/// <summary>
/// One accessibility node of a top-level: its <see cref="Role"/> (Avalonia's <c>AutomationControlType</c> value), its
/// name, value and automation id, its states and actions, its bounds in device-independent pixels from the top-level's
/// corner, and its children's ids in order. Ids are the runner's, unique within the top-level.
/// </summary>
public sealed record SurfaceNode(int Id, int Role, string Name, string Value, string AutomationId, SurfaceNodeStates States,
    double X, double Y, double Width, double Height, List<int> Children);

/// <summary>
/// A change to the accessibility tree of the surface or its <see cref="Popup"/>: the nodes added or changed, whole, and
/// the ids removed. <see cref="Reset"/> replaces the tree with <see cref="Nodes"/>. <see cref="Root"/> is the
/// top-level's own node, whose children a client shows as its control's.
/// </summary>
public sealed record SurfaceAutomation(int Surface, int Popup, int Root, bool Reset, List<SurfaceNode> Nodes, List<int> Removed);

/// <summary>Focus enters the surface (from the end when <see cref="Backward"/>) or leaves it.</summary>
public sealed record SurfaceFocus(int Surface, bool Enter, bool Backward, bool Pointer);

/// <summary>The client's theme changed: the kit theme as JSON (colors as <c>#aarrggbb</c>).</summary>
public sealed record SurfaceTheme(string Json);

/// <summary>The client no longer shows the surface; the runner closes its top-level.</summary>
public sealed record SurfaceClose(int Surface);

/// <summary>
/// One message of a frame of the surface, or of its open <see cref="Popup"/> (0 is the surface itself): its moves (in
/// its first message only), then tiles whose bytes follow one another in the message's byte payload.
/// <see cref="DesiredWidth"/> and <see cref="DesiredHeight"/> are the size the plugin's control asks for at the
/// surface's width, in device-independent pixels. Every message of a <see cref="Keyframe"/> says so: it repaints the
/// whole surface from transparent, and answers <see cref="SurfaceKeyframe"/>. A popup's first frame is a keyframe.
/// </summary>
public sealed record SurfaceFrameHeader(int Surface, long Sequence, int Width, int Height, double Scale, double DesiredWidth, double DesiredHeight,
    bool Last, List<SurfaceMove> Moves, List<SurfaceTile> Tiles, bool Keyframe = false, int Popup = 0);

/// <summary>
/// The client lost what it held of the surface (it reconnected): the runner sends the next frame of the surface and of
/// each of its popups as a keyframe, without waiting for the frame in flight to be acknowledged.
/// </summary>
public sealed record SurfaceKeyframe(int Surface);

/// <summary>A region of the previous frame shifted by (<see cref="Dx"/>, <see cref="Dy"/>) device pixels, clipped to itself: scrolling.</summary>
public sealed record SurfaceMove(int X, int Y, int Width, int Height, int Dx, int Dy);

/// <summary>One tile in device pixels and the length of its bytes.</summary>
public sealed record SurfaceTile(int X, int Y, int Width, int Height, TileCodecKind Codec, int Length);

/// <summary>Tab left the plugin's last focusable element (or its first, <see cref="Backward"/>): the client moves focus on.</summary>
public sealed record SurfaceFocusLeave(int Surface, bool Backward);

/// <summary>What a client hears from one surface: frames, popups opening, moving and closing, and focus leaving it.</summary>
public abstract record SurfaceEvent;

/// <summary>A popup of the surface opened, moved or closed; its frames follow its opening and end with its closing.</summary>
public sealed record SurfacePopupChanged(SurfacePopup Placement) : SurfaceEvent;

/// <summary>A frame message the host checked: its header and its tiles' bytes.</summary>
public sealed record SurfaceFrame(SurfaceFrameHeader Header, byte[] Data) : SurfaceEvent;

/// <summary>Focus left the surface.</summary>
public sealed record SurfaceLeft(bool Backward) : SurfaceEvent;

/// <summary>The cursor over the surface or one of its popups changed.</summary>
public sealed record SurfaceCursorChanged(SurfaceCursor Cursor) : SurfaceEvent;

/// <summary>A text field of the surface or one of its popups gained or lost focus, or its caret moved.</summary>
public sealed record SurfaceImeChanged(SurfaceIme Ime) : SurfaceEvent;

/// <summary>The accessibility tree of the surface or one of its popups changed.</summary>
public sealed record SurfaceAutomationChanged(SurfaceAutomation Automation) : SurfaceEvent;

/// <summary>
/// The client's end of one plugin surface. <see cref="Events"/> completes when the surface closes, with an exception
/// when the plugin broke the protocol or its process ended. Disposing closes it.
/// </summary>
public interface IPluginSurface : IAsyncDisposable
{
    ChannelReader<SurfaceEvent> Events { get; }

    /// <summary>The number the host gave the surface; 0 while a remote client has not heard it yet.</summary>
    int Number { get; }
    void Resize(double width, double height, double scale);
    void Input(SurfaceInput input);
    /// <summary>The client applied frame <paramref name="sequence"/> of the surface, or of its popup <paramref name="popup"/>.</summary>
    void Acknowledge(long sequence, int popup);
    void Focus(bool enter, bool backward, bool pointer);

    /// <summary>The user pressed outside the surface and its popups: the popups that close on such a press close.</summary>
    void Dismiss();

    /// <summary>The client's kit theme changed. The plugin's process holds one theme, the last any client pushed.</summary>
    void Theme(string json);

    /// <summary>
    /// The client no longer knows what it holds: frames up to the next keyframe are dropped, and the next frame is one,
    /// sent without waiting for the acknowledgement of the frame in flight.
    /// </summary>
    void Keyframe();
}

/// <summary>Opens surfaces for the factories one client's context lent.</summary>
public interface IPluginSurfaceService
{
    /// <summary>
    /// Calls the factory behind <paramref name="handle"/> with its arguments, under the client's kit theme
    /// <paramref name="theme"/>, and answers the surface showing its control.
    /// </summary>
    IPluginSurface Open(int member, long handle, string payload, string theme);
}

/// <summary>The surface protocol's members on the plugin channel and their arguments' encoding.</summary>
public static class SurfaceProtocol
{
    /// <summary>Host to runner, a call answered once the control is shown: <see cref="SurfaceOpen"/>.</summary>
    public static readonly int Open = IsoMember.Id("surface.open");
    /// <summary>Host to runner, one-ways: <see cref="SurfaceResize"/>, <see cref="SurfaceInput"/>, <see cref="SurfaceAck"/>, <see cref="SurfaceFocus"/>, <see cref="SurfaceTheme"/>, <see cref="SurfaceClose"/>.</summary>
    public static readonly int Resize = IsoMember.Id("surface.resize");
    public static readonly int Input = IsoMember.Id("surface.input");
    public static readonly int Ack = IsoMember.Id("surface.ack");
    public static readonly int Focus = IsoMember.Id("surface.focus");
    public static readonly int Theme = IsoMember.Id("surface.theme");
    public static readonly int Close = IsoMember.Id("surface.close");
    /// <summary>Host to runner, one-ways: <see cref="SurfaceKeyframe"/> and <see cref="SurfaceDismiss"/>.</summary>
    public static readonly int Keyframe = IsoMember.Id("surface.keyframe");
    public static readonly int Dismiss = IsoMember.Id("surface.dismiss");
    /// <summary>
    /// Runner to host, one-ways on the client's context: <see cref="SurfaceFrameHeader"/> with the tiles' bytes,
    /// <see cref="SurfaceFocusLeave"/> and <see cref="SurfacePopup"/>; then <see cref="Cursor"/> and <see cref="Ime"/>.
    /// </summary>
    public static readonly int Frame = IsoMember.Id("surface.frame");
    public static readonly int FocusLeave = IsoMember.Id("surface.focus-leave");
    public static readonly int Popup = IsoMember.Id("surface.popup");
    /// <summary>Runner to host, one-ways on the client's context: <see cref="SurfaceCursor"/> and <see cref="SurfaceIme"/>.</summary>
    public static readonly int Cursor = IsoMember.Id("surface.cursor");
    public static readonly int Ime = IsoMember.Id("surface.ime");
    /// <summary>Runner to host, a one-way on the client's context: <see cref="SurfaceAutomation"/>.</summary>
    public static readonly int Automation = IsoMember.Id("surface.automation");
    /// <summary>
    /// Host to runner, answered only when the runner was started with <see cref="DiagnosticsVariable"/> set (the checks):
    /// a fresh render of a surface and the bounds of its named controls (<see cref="SurfaceCapture"/>).
    /// </summary>
    public static readonly int Capture = IsoMember.Id("surface.capture");
    public const string DiagnosticsVariable = "SHARPRAIL_RUNNER_DIAGNOSTICS";

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)SurfaceJsonContext.Default.GetTypeInfo(typeof(T))!);

    /// <summary>Reads a surface message; anything malformed is <see cref="IsoError.InvalidParams"/>.</summary>
    public static T Decode<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)SurfaceJsonContext.Default.GetTypeInfo(typeof(T))!)
                ?? throw new IsoCallException(IsoError.InvalidParams, "Empty arguments.");
        }
        catch (JsonException error) { throw new IsoCallException(IsoError.InvalidParams, "Invalid arguments: " + error.Message); }
    }
}

/// <summary>
/// A surface's fresh render (premultiplied BGRA, base64), its named controls' bounds in device-independent pixels and,
/// for a named text field, its text and (under its name with <c>.preedit</c>) the composition it shows; and the top-level's
/// accessibility tree as the runner reads it, one line per node.
/// </summary>
public sealed record SurfaceCapture(int Width, int Height, string Pixels, Dictionary<string, double[]> Bounds, Dictionary<string, string>? Texts = null,
    List<string>? Automation = null);

/// <summary>Asks for a <see cref="SurfaceCapture"/> of a surface, or of its open <see cref="Popup"/>, with the bounds of these named controls.</summary>
public sealed record SurfaceCaptureRequest(int Surface, List<string> Names, int Popup = 0);

[JsonSourceGenerationOptions(UseStringEnumConverter = true, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SurfaceOpen))]
[JsonSerializable(typeof(SurfaceResize))]
[JsonSerializable(typeof(SurfaceInput))]
[JsonSerializable(typeof(SurfaceAck))]
[JsonSerializable(typeof(SurfaceFocus))]
[JsonSerializable(typeof(SurfaceTheme))]
[JsonSerializable(typeof(SurfaceClose))]
[JsonSerializable(typeof(SurfaceKeyframe))]
[JsonSerializable(typeof(SurfaceDismiss))]
[JsonSerializable(typeof(SurfacePopup))]
[JsonSerializable(typeof(SurfaceFrameHeader))]
[JsonSerializable(typeof(SurfaceFocusLeave))]
[JsonSerializable(typeof(SurfaceCursor))]
[JsonSerializable(typeof(SurfaceIme))]
[JsonSerializable(typeof(SurfaceAutomation))]
[JsonSerializable(typeof(SurfaceCapture))]
[JsonSerializable(typeof(SurfaceCaptureRequest))]
internal sealed partial class SurfaceJsonContext : JsonSerializerContext;

/// <summary>
/// The caps a frame message is held to before a client applies it (Security.SPEC.md, UI halves): the host's relay holds
/// the plugin's frames to them, and a remote client the host's again.
/// </summary>
public static class SurfaceFrames
{
    /// <summary>Why a frame message breaks the surface protocol, or null when it keeps to it.</summary>
    public static string? Violation(SurfaceFrameHeader header, long bytes, SurfaceFrameState state)
    {
        if (header.Moves is null || header.Tiles is null || header.Moves.Any(move => move is null) || header.Tiles.Any(tile => tile is null))
            return "The plugin sent a frame with a missing part.";
        if (header.Width is < 0 or > SurfaceLimits.MostSide || header.Height is < 0 or > SurfaceLimits.MostSide || (long)header.Width * header.Height > SurfaceLimits.MostFramePixels)
            return "The plugin sent a frame larger than a client takes.";
        if (!(header.Scale is >= SurfaceLimits.LeastScale and <= SurfaceLimits.MostScale)) return "The plugin sent a frame at a scale out of range.";
        if (!(header.DesiredWidth is >= 0 and <= 100_000) || !(header.DesiredHeight is >= 0 and <= 100_000)) return "The plugin asked for a size out of range.";
        if (state.Open)
        {
            if (header.Sequence != state.Sequence || header.Width != state.Width || header.Height != state.Height || header.Keyframe != state.Keyframe)
                return "The plugin changed a frame while sending it.";
            if (header.Moves.Count > 0) return "The plugin sent moves after a frame's first message.";
        }
        else if (header.Keyframe && state.KeyframePending)
        {
            if (header.Sequence <= state.Sequence) return "The plugin sent frames out of order.";
        }
        else
        {
            if (state.Unacknowledged) return "The plugin sent a frame before the client took the last one.";
            if (header.Sequence != state.Sequence + 1) return "The plugin sent frames out of order.";
        }
        if (header.Moves.Count > 64) return "The plugin sent too many moves.";
        if (header.Moves.Count > 0 && (header.Keyframe || header.Width != state.Width || header.Height != state.Height)) return "The plugin moved pixels of a frame of another size.";
        if (header.Moves.Any(move => !SurfacePixels.Fits(move, header.Width, header.Height))) return "The plugin sent a move outside its surface.";
        if (header.Tiles.Count > SurfaceLimits.MostTiles) return "The plugin sent too many tiles.";
        long length = 0, pixels = state.Open ? state.Pixels : 0;
        foreach (var tile in header.Tiles)
        {
            if (tile.Width is < 1 or > SurfaceLimits.MostTileSide || tile.Height is < 1 or > SurfaceLimits.MostTileSide || tile.X < 0 || tile.Y < 0 ||
                tile.X + tile.Width > header.Width || tile.Y + tile.Height > header.Height)
                return "The plugin sent a tile outside its surface.";
            var valid = tile.Codec switch
            {
                TileCodecKind.Solid => tile.Length == 4,
                // QOI writes at most 5 bytes a pixel, so one tile always fits in one message.
                TileCodecKind.Qoi => tile.Length > 0 && tile.Length <= 5L * tile.Width * tile.Height,
                _ => false,
            };
            if (!valid) return "The plugin sent a tile the client cannot read.";
            length += tile.Length;
            pixels += (long)tile.Width * tile.Height;
        }
        if (length != bytes || bytes > 2 * SurfaceLimits.MessageBytes) return "The plugin's tiles do not match their bytes.";
        if (pixels > SurfaceLimits.MostFramePixels) return "The plugin sent a frame larger than a client takes.";
        return null;
    }
}

/// <summary>
/// Where a surface's frames stand: the last frame's number and size, the one being received, whether the client has
/// taken the last one, and whether a keyframe is awaited. Not thread-safe; its owner locks.
/// </summary>
public sealed class SurfaceFrameState
{
    public long Sequence { get; set; }
    public int Width { get; set; } = -1;
    public int Height { get; set; } = -1;
    public bool Open { get; set; }
    public bool Keyframe { get; set; }
    public bool Unacknowledged { get; set; }
    public long Pixels { get; set; }
    public bool KeyframePending { get; set; }
    /// <summary>Whether these are a dialog's frames.</summary>
    public bool Dialog { get; init; }

    /// <summary>
    /// A frame message sent before the keyframe was asked for, which its receiver drops: a keyframe repaints what it
    /// would have.
    /// </summary>
    public bool Stale(SurfaceFrameHeader header) => KeyframePending && !Open && !header.Keyframe;

    /// <summary>Records a frame message that kept to the caps.</summary>
    public void Accept(SurfaceFrameHeader header)
    {
        if (!Open)
        {
            Pixels = 0;
            if (header.Keyframe) KeyframePending = false;
        }
        (Sequence, Width, Height, Open, Keyframe) = (header.Sequence, header.Width, header.Height, !header.Last, header.Keyframe);
        Pixels += header.Tiles.Sum(tile => (long)tile.Width * tile.Height);
        if (header.Last) Unacknowledged = true;
    }

    /// <summary>The client took frame <paramref name="sequence"/>: true when that releases the next one.</summary>
    public bool Acknowledge(long sequence)
    {
        if (sequence != Sequence || Open || !Unacknowledged) return false;
        Unacknowledged = false;
        return true;
    }
}

/// <summary>
/// Where a surface and its open popups stand: one <see cref="SurfaceFrameState"/> each (the surface's under 0), and the
/// popups' numbers so far. Not thread-safe; its owner locks.
/// </summary>
public sealed class SurfaceState
{
    private readonly Dictionary<int, SurfaceFrameState> frames = new() { [0] = new() };
    private int lastPopup;
    // The accessibility tree of the surface (0) and of each open popup, by node id.
    private readonly Dictionary<int, Dictionary<int, SurfaceNode>> trees = [];

    /// <summary>The frames of the surface (0) or of an open popup; null for a popup that is not open.</summary>
    public SurfaceFrameState? Frames(int popup) => frames.GetValueOrDefault(popup);

    /// <summary>The surface's frames and each open popup's.</summary>
    public IEnumerable<SurfaceFrameState> All => frames.Values;

    /// <summary>
    /// Why a popup's opening, move or closing breaks the surface protocol, or null when it keeps to it and is recorded.
    /// A popup opened while the surface awaits a keyframe awaits one too.
    /// </summary>
    public string? Place(SurfacePopup popup)
    {
        if (popup.Popup < 1) return "The plugin numbered a popup below 1.";
        if (!popup.Open)
        {
            if (!frames.Remove(popup.Popup)) return "The plugin closed a popup that is not open.";
            trees.Remove(popup.Popup);
            return null;
        }
        if (!(Math.Abs(popup.X) <= SurfaceLimits.MostSide) || !(Math.Abs(popup.Y) <= SurfaceLimits.MostSide) ||
            !(popup.Width is > 0 and <= SurfaceLimits.MostSide) || !(popup.Height is > 0 and <= SurfaceLimits.MostSide))
            return "The plugin placed a popup out of range.";
        if (popup.Title is null || popup.Title.Length > SurfaceLimits.MostTitle || popup.Title.Any(char.IsControl) || popup.Title.Length > 0 && !popup.Dialog)
            return "The plugin titled a popup with text a client does not take.";
        if (frames.TryGetValue(popup.Popup, out var known) && known.Dialog != popup.Dialog) return "The plugin turned a popup into a dialog or back.";
        if (frames.ContainsKey(popup.Popup)) return null;
        if (popup.Popup <= lastPopup) return "The plugin opened a popup under a number it used before.";
        if (frames.Count > SurfaceLimits.MostPopups) return "The plugin opened too many popups.";
        lastPopup = popup.Popup;
        frames[popup.Popup] = new SurfaceFrameState { KeyframePending = frames[0].KeyframePending, Dialog = popup.Dialog };
        return null;
    }

    /// <summary>Why a cursor report breaks the surface protocol, or null.</summary>
    public string? Cursor(SurfaceCursor cursor)
    {
        if (!frames.ContainsKey(cursor.Popup)) return "The plugin set a cursor on a popup that is not open.";
        return cursor.Shape is >= 0 and <= SurfaceLimits.MostCursorShape ? null : "The plugin asked for a cursor a client does not know.";
    }

    /// <summary>Why an input method report breaks the surface protocol, or null.</summary>
    public string? Ime(SurfaceIme ime)
    {
        if (!frames.ContainsKey(ime.Popup)) return "The plugin placed a caret on a popup that is not open.";
        return Math.Abs(ime.X) <= SurfaceLimits.MostSide && Math.Abs(ime.Y) <= SurfaceLimits.MostSide &&
            ime.Width is >= 0 and <= SurfaceLimits.MostSide && ime.Height is >= 0 and <= SurfaceLimits.MostSide
            ? null : "The plugin placed a caret out of range.";
    }

    /// <summary>
    /// Why an accessibility change breaks the surface protocol, or null when it keeps to it and is recorded: every
    /// node is reached from the root once, within the caps on nodes, depth and text, with a known role and bounds in range.
    /// </summary>
    public string? Automation(SurfaceAutomation automation)
    {
        if (!frames.ContainsKey(automation.Popup)) return "The plugin described a popup that is not open.";
        if (automation.Nodes is null || automation.Removed is null || automation.Nodes.Count > SurfaceLimits.MostNodes || automation.Removed.Count > SurfaceLimits.MostNodes)
            return "The plugin described too many accessibility nodes.";
        var tree = automation.Reset || !trees.TryGetValue(automation.Popup, out var known) ? [] : new Dictionary<int, SurfaceNode>(known);
        foreach (var gone in automation.Removed)
            if (!tree.Remove(gone)) return "The plugin removed an accessibility node it never described.";
        foreach (var node in automation.Nodes)
        {
            if (node is null) return "The plugin described an empty accessibility node.";
            if (Node(node) is { } broken) return broken;
            tree[node.Id] = node;
        }
        if (tree.Count > SurfaceLimits.MostNodes) return "The plugin described too many accessibility nodes.";
        if (tree.Count > 0 || automation.Root != 0)
        {
            if (!tree.ContainsKey(automation.Root)) return "The plugin's accessibility tree has no root.";
            var depths = new Dictionary<int, int> { [automation.Root] = 0 };
            var pending = new Queue<int>([automation.Root]);
            while (pending.TryDequeue(out var id))
                foreach (var child in tree[id].Children)
                {
                    if (!tree.ContainsKey(child) || !depths.TryAdd(child, depths[id] + 1)) return "The plugin's accessibility tree is not a tree.";
                    if (depths[child] > SurfaceLimits.MostNodeDepth) return "The plugin's accessibility tree nests too deep.";
                    pending.Enqueue(child);
                }
            if (depths.Count != tree.Count) return "The plugin's accessibility tree holds nodes outside it.";
        }
        trees[automation.Popup] = tree;
        return null;
    }

    /// <summary>
    /// Whether an accessibility action from a client acts on a node the plugin described, as the plugin's process would find
    /// it: the top-level is open, the node is in its tree and offers the action, and a value is plain text within the cap.
    /// Anything else is stale (the tree moved on) and dropped. Other input is not judged here.
    /// </summary>
    public bool Admits(SurfaceInput input)
    {
        var needed = input.Kind switch
        {
            SurfaceInputKind.Invoke => SurfaceNodeStates.Invoke,
            SurfaceInputKind.SetFocus => SurfaceNodeStates.Focusable,
            SurfaceInputKind.SetValue => SurfaceNodeStates.Value,
            SurfaceInputKind.Expand or SurfaceInputKind.Collapse => SurfaceNodeStates.ExpandCollapse,
            SurfaceInputKind.Toggle => SurfaceNodeStates.Toggle,
            _ => (SurfaceNodeStates?)null
        };
        if (needed is not { } action) return true;
        if (!trees.TryGetValue(input.Popup, out var tree) || !tree.TryGetValue(input.Node, out var node) || !node.States.HasFlag(action)) return false;
        return input.Kind != SurfaceInputKind.SetValue || input.Text is { } text && text.Length <= SurfaceLimits.MostNodeText && !text.Any(char.IsControl);
    }

    private static string? Node(SurfaceNode node)
    {
        static bool Plain(string? text, int most) => text is not null && text.Length <= most && !text.Any(char.IsControl);
        if (node.Id < 1 || node.Children is null || node.Children.Count > SurfaceLimits.MostNodes) return "The plugin described an accessibility node out of range.";
        if (node.Role is < 0 or > SurfaceLimits.MostRole) return "The plugin gave an accessibility node a role a client does not know.";
        if (!Plain(node.Name, SurfaceLimits.MostNodeText) || !Plain(node.Value, SurfaceLimits.MostNodeText) || !Plain(node.AutomationId, SurfaceLimits.MostAutomationId))
            return "The plugin named an accessibility node with text a client does not take.";
        if ((node.States & ~SurfaceLimits.KnownNodeStates) != 0) return "The plugin gave an accessibility node a state a client does not know.";
        return Math.Abs(node.X) <= SurfaceLimits.MostSide && Math.Abs(node.Y) <= SurfaceLimits.MostSide &&
            node.Width is >= 0 and <= SurfaceLimits.MostSide && node.Height is >= 0 and <= SurfaceLimits.MostSide
            ? null : "The plugin placed an accessibility node out of range.";
    }

    /// <summary>Why a frame message breaks the surface protocol (it names a popup that is not open, or breaks the caps), or null.</summary>
    public string? Violation(SurfaceFrameHeader header, long bytes) =>
        Frames(header.Popup) is { } state ? SurfaceFrames.Violation(header, bytes, state) : "The plugin sent a frame for a popup that is not open.";

    /// <summary>A frame message sent before a keyframe was asked for: its receiver drops it.</summary>
    public bool Stale(SurfaceFrameHeader header) => Frames(header.Popup)?.Stale(header) ?? false;

    /// <summary>Every frame state awaits a keyframe; with <paramref name="lost"/>, a frame half received is forgotten.</summary>
    public void AwaitKeyframe(bool lost)
    {
        foreach (var state in frames.Values)
        {
            state.KeyframePending = true;
            if (lost) (state.Open, state.Unacknowledged) = (false, false);
        }
    }
}

/// <summary>Moves applied the same way on both ends, so the client's pixels stay the runner's.</summary>
public static class SurfacePixels
{
    /// <summary>Whether a move lies inside a surface of this size and shifts by less than itself.</summary>
    public static bool Fits(SurfaceMove move, int width, int height) =>
        move is { X: >= 0, Y: >= 0, Width: > 0, Height: > 0 } && move.X + (long)move.Width <= width && move.Y + (long)move.Height <= height &&
        Math.Abs((long)move.Dx) < move.Width && Math.Abs((long)move.Dy) < move.Height && (move.Dx != 0 || move.Dy != 0);

    /// <summary>Shifts the move's region of premultiplied BGRA pixels by its offset; what it uncovers keeps its old pixels.</summary>
    public static void Move(Span<byte> pixels, int stride, SurfaceMove move)
    {
        var columns = move.Width - Math.Abs(move.Dx);
        var from = move.X + Math.Max(0, -move.Dx);
        var to = move.X + Math.Max(0, move.Dx);
        var rows = move.Height - Math.Abs(move.Dy);
        for (var step = 0; step < rows; step++)
        {
            var row = move.Dy > 0 ? rows - 1 - step : step;
            var source = move.Y + Math.Max(0, -move.Dy) + row;
            var target = move.Y + Math.Max(0, move.Dy) + row;
            pixels.Slice(source * stride + from * 4, columns * 4).CopyTo(pixels.Slice(target * stride + to * 4, columns * 4));
        }
    }
}