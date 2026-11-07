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
    public const double LeastScale = 0.25, MostScale = 8;
}

/// <summary>What a forwarded input event is.</summary>
public enum SurfaceInputKind { None, PointerMove, PointerDown, PointerUp, PointerLeave, Wheel, KeyDown, KeyUp, Text }

/// <summary>
/// The host asks the runner to call a factory the plugin lent on a client's context and show its control as
/// <see cref="Surface"/>, a number the host chose. <see cref="Theme"/> is the client's kit theme, applied first.
/// </summary>
public sealed record SurfaceOpen(long Context, int Surface, int Member, long Handle, string Payload, string Theme);

/// <summary>A surface's size in device-independent pixels at the client's scale. Each new size starts from a transparent frame.</summary>
public sealed record SurfaceResize(int Surface, double Width, double Height, double Scale);

/// <summary>
/// One input event at a point in device-independent pixels. <see cref="Button"/>, <see cref="Key"/>,
/// <see cref="PhysicalKey"/> and <see cref="Modifiers"/> carry Avalonia's own values.
/// </summary>
public sealed record SurfaceInput(int Surface, SurfaceInputKind Kind, double X = 0, double Y = 0, int Button = 0, int Modifiers = 0,
    double DeltaX = 0, double DeltaY = 0, int Key = 0, int PhysicalKey = 0, string Text = "");

/// <summary>The client applied frame <see cref="Sequence"/>; the runner may send the next.</summary>
public sealed record SurfaceAck(int Surface, long Sequence);

/// <summary>Focus enters the surface (from the end when <see cref="Backward"/>) or leaves it.</summary>
public sealed record SurfaceFocus(int Surface, bool Enter, bool Backward, bool Pointer);

/// <summary>The client's theme changed: the kit theme as JSON (colors as <c>#aarrggbb</c>).</summary>
public sealed record SurfaceTheme(string Json);

/// <summary>The client no longer shows the surface; the runner closes its top-level.</summary>
public sealed record SurfaceClose(int Surface);

/// <summary>
/// One message of a frame: its moves (in its first message only), then tiles whose bytes follow one another in the
/// message's byte payload. <see cref="DesiredWidth"/> and <see cref="DesiredHeight"/> are the size the plugin's control
/// asks for at the surface's width, in device-independent pixels. Every message of a <see cref="Keyframe"/> says so:
/// it repaints the whole surface from transparent, and answers <see cref="SurfaceKeyframe"/>.
/// </summary>
public sealed record SurfaceFrameHeader(int Surface, long Sequence, int Width, int Height, double Scale, double DesiredWidth, double DesiredHeight,
    bool Last, List<SurfaceMove> Moves, List<SurfaceTile> Tiles, bool Keyframe = false);

/// <summary>
/// The client lost what it held of the surface (it reconnected): the runner sends the next frame as a keyframe, without
/// waiting for the frame in flight to be acknowledged.
/// </summary>
public sealed record SurfaceKeyframe(int Surface);

/// <summary>A region of the previous frame shifted by (<see cref="Dx"/>, <see cref="Dy"/>) device pixels, clipped to itself: scrolling.</summary>
public sealed record SurfaceMove(int X, int Y, int Width, int Height, int Dx, int Dy);

/// <summary>One tile in device pixels and the length of its bytes.</summary>
public sealed record SurfaceTile(int X, int Y, int Width, int Height, TileCodecKind Codec, int Length);

/// <summary>Tab left the plugin's last focusable element (or its first, <see cref="Backward"/>): the client moves focus on.</summary>
public sealed record SurfaceFocusLeave(int Surface, bool Backward);

/// <summary>What a client hears from one surface: frames and focus leaving it.</summary>
public abstract record SurfaceEvent;

/// <summary>A frame message the host checked: its header and its tiles' bytes.</summary>
public sealed record SurfaceFrame(SurfaceFrameHeader Header, byte[] Data) : SurfaceEvent;

/// <summary>Focus left the surface.</summary>
public sealed record SurfaceLeft(bool Backward) : SurfaceEvent;

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
    void Acknowledge(long sequence);
    void Focus(bool enter, bool backward, bool pointer);

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
    /// <summary>Host to runner, a one-way: <see cref="SurfaceKeyframe"/>.</summary>
    public static readonly int Keyframe = IsoMember.Id("surface.keyframe");
    /// <summary>Runner to host, one-ways on the client's context: <see cref="SurfaceFrameHeader"/> with the tiles' bytes, and <see cref="SurfaceFocusLeave"/>.</summary>
    public static readonly int Frame = IsoMember.Id("surface.frame");
    public static readonly int FocusLeave = IsoMember.Id("surface.focus-leave");
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

/// <summary>A surface's fresh render (premultiplied BGRA, base64) and its named controls' bounds in device-independent pixels.</summary>
public sealed record SurfaceCapture(int Width, int Height, string Pixels, Dictionary<string, double[]> Bounds);

/// <summary>Asks for a <see cref="SurfaceCapture"/> of a surface with the bounds of these named controls.</summary>
public sealed record SurfaceCaptureRequest(int Surface, List<string> Names);

[JsonSourceGenerationOptions(UseStringEnumConverter = true, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SurfaceOpen))]
[JsonSerializable(typeof(SurfaceResize))]
[JsonSerializable(typeof(SurfaceInput))]
[JsonSerializable(typeof(SurfaceAck))]
[JsonSerializable(typeof(SurfaceFocus))]
[JsonSerializable(typeof(SurfaceTheme))]
[JsonSerializable(typeof(SurfaceClose))]
[JsonSerializable(typeof(SurfaceKeyframe))]
[JsonSerializable(typeof(SurfaceFrameHeader))]
[JsonSerializable(typeof(SurfaceFocusLeave))]
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