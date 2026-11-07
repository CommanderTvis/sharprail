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
/// asks for at the surface's width, in device-independent pixels.
/// </summary>
public sealed record SurfaceFrameHeader(int Surface, long Sequence, int Width, int Height, double Scale, double DesiredWidth, double DesiredHeight,
    bool Last, List<SurfaceMove> Moves, List<SurfaceTile> Tiles);

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
    void Resize(double width, double height, double scale);
    void Input(SurfaceInput input);
    void Acknowledge(long sequence);
    void Focus(bool enter, bool backward, bool pointer);

    /// <summary>The client's kit theme changed. The plugin's process holds one theme, the last any client pushed.</summary>
    void Theme(string json);
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
[JsonSerializable(typeof(SurfaceFrameHeader))]
[JsonSerializable(typeof(SurfaceFocusLeave))]
[JsonSerializable(typeof(SurfaceCapture))]
[JsonSerializable(typeof(SurfaceCaptureRequest))]
internal sealed partial class SurfaceJsonContext : JsonSerializerContext;

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